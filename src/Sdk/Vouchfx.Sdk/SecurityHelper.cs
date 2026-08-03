// Vouchfx.Sdk — SecurityHelper (authenticated-infrastructure-mtls, slice D — REQ-024).
//
// Provides the compile-time constant source for the Security_Helpers static class that the
// HTTP-family providers splice into their RequiredHelpers set, exactly mirroring
// SecretHelper (S05-B-02) and SubstituteHelper (S04-B-03). Deduplication is handled by the
// CsxAssembler helper-dedup logic (§13.3.1), so a suite whose steps span http.rest,
// http.soap and metrics-assert.prometheus carries ONE copy of this class, not three.
//
// Why a shared helper rather than three copies. All three providers need the same logic:
// decide from the DECLARED PROFILE whether a client identity is presented, present it, and
// rebuild the server chain against the declared CA. Three copies of a transport-security
// decision are three places for one of them to be subtly weakened — the failure mode that
// matters here is a copy that forgives a name mismatch, one that sets a custom validation
// callback even when no CA was declared and so silently replaces the platform's own verdict,
// or one whose profile switch falls open instead of closed. One source removes the possibility.
//
// Design constraints (§13.3.1), identical to SecretHelper's:
//   • Static class prefixed 'Security_' so it cannot collide with a provider-specific helper.
//   • Every type fully-qualified — the helper compiles independently of any 'using' ordering
//     in the surrounding script. The Vouchfx.Engine.Abstractions.Security types resolve
//     because RoslynScriptCompiler references Vouchfx.Engine.Abstractions.
//   • No 'using var' — prohibited in a Roslyn script body.
//   • Byte-identical across providers so CsxAssembler dedupes it to one copy.
//   • No per-step interpolation: everything that varies is a runtime argument.
namespace Vouchfx.Sdk;

/// <summary>
/// Supplies the canonical source text for the <c>Security_Helpers</c> static class the
/// HTTP-family providers splice into their <see cref="CsxFragment.RequiredHelpers"/> to
/// configure an <c>HttpClientHandler</c> from the client security configuration declared for
/// a step's own <c>target</c> (authenticated-infrastructure-mtls, REQ-024).
/// </summary>
/// <remarks>
/// <para>
/// The material is read at STEP-EXECUTION time through
/// <c>ScriptGlobalVariables.Security</c> (REQ-014), never at compile time: baking a
/// certificate path — let alone its contents — into the emitted script would defeat
/// compile-once and corrupt the reproducibility envelope, the same rule §17 already imposes
/// on secrets.
/// </para>
/// <para>
/// A target that declares no <c>security</c> block resolves to <see langword="null"/> and the
/// handler is left exactly as the provider built it, so an unsecured suite is unaffected.
/// </para>
/// <para>
/// <strong>The declared profile decides what happens, and an unknown profile fails closed.</strong>
/// <c>mtls</c> presents the declared client certificate and requires one; <c>tls</c> presents
/// none and refuses a contradictory <c>clientCert</c>/<c>clientKey</c>; any other profile throws
/// <c>SecurityMaterialException</c>. Because REQ-019
/// makes the profile discriminator OPEN, inferring the behaviour from which certificate fields
/// happen to be non-null would let a later profile inherit HTTPS client-certificate semantics
/// nobody chose for it — see the switch's own remarks in <see cref="Source"/>.
/// </para>
/// </remarks>
public static class SecurityHelper
{
    /// <summary>
    /// The full C# source text of the <c>Security_Helpers</c> static class.
    /// </summary>
    /// <remarks>
    /// Paste this constant as a single element of
    /// <see cref="CsxFragment.RequiredHelpers"/>; the assembler deduplicates it across every
    /// step in the suite.
    /// </remarks>
    public const string Source =
        "static class Security_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Configures <paramref name=\"handler\"/> from the client security configuration\n" +
        "    /// declared for <paramref name=\"targetName\"/> (REQ-024): presents the declared\n" +
        "    /// client certificate under the profiles that call for one, and trusts the declared\n" +
        "    /// CA as a custom root. A target with no declared security leaves the handler\n" +
        "    /// untouched.\n" +
        "    /// </summary>\n" +
        "    public static void ConfigureHandler(\n" +
        "        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,\n" +
        "        string targetName,\n" +
        "        System.Net.Http.HttpClientHandler handler)\n" +
        "    {\n" +
        "        var configuration = security?.For(targetName);\n" +
        "        if (configuration == null)\n" +
        "        {\n" +
        "            // No security declared for this target. Configure NOTHING: the platform's own\n" +
        "            // trust store applies and no client identity is presented (REQ-001/REQ-024 —\n" +
        "            // an absent security block is a normal configuration, never a defaulted one).\n" +
        "            return;\n" +
        "        }\n" +
        "\n" +
        "        // WHAT PRESENTS A CLIENT IDENTITY IS THE DECLARED PROFILE, NEVER WHICH FIELDS\n" +
        "        // HAPPEN TO BE NON-NULL. `profile` is the MECHANISM SELECTOR (the same role\n" +
        "        // `type: family.provider` plays for a step), so the mapping from profile to HTTPS\n" +
        "        // transport semantics is written out here, exhaustively, and anything absent from\n" +
        "        // it FAILS CLOSED.\n" +
        "        //\n" +
        "        // Why that matters even though slice C's SecurityProfileRegistry already refuses\n" +
        "        // an unregistered profile before a suite ever reaches here: REQ-019 makes the\n" +
        "        // discriminator OPEN, so a LATER profile — wired by that registry for its own\n" +
        "        // technology, and carrying certificate paths because most transports need\n" +
        "        // some — would, under a which-fields-are-set test, silently acquire HTTPS\n" +
        "        // client-certificate semantics nobody decided it should have. A profile this\n" +
        "        // helper has not been taught about is an unanswered question, not a default.\n" +
        "        //\n" +
        "        // A FUTURE PROFILE AUTHOR MUST EDIT THIS SWITCH. Registering a wiring in\n" +
        "        // SecurityProfileRegistry makes a profile declarable; it does not decide what an\n" +
        "        // HTTP-family step does with it. Add the profile here with an explicit decision\n" +
        "        // about whether it presents a client identity — the two lists are pinned equal by\n" +
        "        // SecurityProfileRegistryTests, so a profile added to the registry alone turns the\n" +
        "        // suite red rather than inheriting these semantics by accident.\n" +
        "        var profile = configuration.Profile;\n" +
        "        var certificates = configuration.Certificates;\n" +
        "\n" +
        "        if (string.Equals(profile, \"mtls\", System.StringComparison.Ordinal))\n" +
        "        {\n" +
        "            // Reading ClientCertificate LOADS the declared files. That read happens here,\n" +
        "            // inside the caller's guarded region, so a file that exists (already checked\n" +
        "            // by EnvironmentSecurityValidator pre-topology) but is malformed surfaces as a\n" +
        "            // step-scoped EnvironmentError naming the field, rather than as an opaque\n" +
        "            // handshake failure raised from inside a TLS validation callback.\n" +
        "            var clientCertificate = certificates?.ClientCertificate;\n" +
        "            if (clientCertificate == null)\n" +
        "            {\n" +
        "                // Declared mutual TLS, nothing to present. Continuing would connect with\n" +
        "                // NO client identity, and against a listener that requests but does not\n" +
        "                // require one that is a green suite which authenticated nothing — the\n" +
        "                // same measured failure mode SecurityConfigurationAccessor's half-pair\n" +
        "                // check exists to close, reached by the other half of the same route.\n" +
        "                throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(\n" +
        "                    \"target '\" + targetName + \"': profile 'mtls' requires a client identity, but no \" +\n" +
        "                    \"'clientCert'/'clientKey' pair resolved for this target. Declare both, or use \" +\n" +
        "                    \"'profile: tls' to present no client identity.\");\n" +
        "            }\n" +
        "\n" +
        "            // A SINGLE LEAF, and that is a real limit rather than an implementation\n" +
        "            // detail. HttpClientHandler.ClientCertificates is a SELECTION set, not a wire\n" +
        "            // chain: SslStream picks one certificate from it and builds what it sends from\n" +
        "            // the host's own stores. MEASURED on this host, three arms, twice, identical —\n" +
        "            // with a client leaf issued by an intermediate and per-run unique subject names\n" +
        "            // so no chain cache could supply the link: leaf alone -> the server's chain\n" +
        "            // arrives with 1 element; leaf PLUS the intermediate added to this same\n" +
        "            // collection -> still 1 element, the intermediate never reaches the wire; that\n" +
        "            // same leaf presented through the ClientCertificateContext of\n" +
        "            // SocketsHttpHandler.SslOptions -> 2 elements, intermediate present. So an\n" +
        "            // INTERMEDIATE-ISSUED client certificate authenticates only if the server can\n" +
        "            // already find the intermediate itself. Widening this collection would NOT\n" +
        "            // change that — the review that proposed it assumed otherwise; closing it needs\n" +
        "            // the handler type changed across all three providers (§3.2.6b records the\n" +
        "            // limit for authors).\n" +
        "            //\n" +
        "            // Worth knowing before that change is costed: it is the SAME change the\n" +
        "            // accessor's AIA note already deferred — both want the handshake driven through\n" +
        "            // SocketsHttpHandler.SslOptions rather than HttpClientHandler (there,\n" +
        "            // CertificateChainPolicy; here, ClientCertificateContext). One handler-type\n" +
        "            // migration closes both, so they should be costed together rather than twice.\n" +
        "            handler.ClientCertificateOptions = System.Net.Http.ClientCertificateOption.Manual;\n" +
        "            handler.ClientCertificates.Add(clientCertificate);\n" +
        "        }\n" +
        "        else if (string.Equals(profile, \"tls\", System.StringComparison.Ordinal))\n" +
        "        {\n" +
        "            // 'tls' means SERVER authentication only. Client material declared alongside it\n" +
        "            // is a contradiction, not a hint: the schema forbids the combination outright,\n" +
        "            // so reaching here means an embedder bypassed it, and the two readings —\n" +
        "            // 'they meant mtls' and 'they meant no client identity' — differ by exactly\n" +
        "            // whether the run authenticates. Refuse rather than pick. Tested on the PATH\n" +
        "            // view so a rejected configuration never loads a key into the host key store.\n" +
        "            if (certificates != null &&\n" +
        "                (certificates.ClientCertificatePath != null || certificates.ClientKeyPath != null))\n" +
        "            {\n" +
        "                throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(\n" +
        "                    \"target '\" + targetName + \"': profile 'tls' presents no client identity, but \" +\n" +
        "                    \"'clientCert'/'clientKey' is declared for it. Use 'profile: mtls' to present the \" +\n" +
        "                    \"declared client certificate, or remove both fields.\");\n" +
        "            }\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(\n" +
        "                \"target '\" + targetName + \"': security profile '\" + profile + \"' has no defined \" +\n" +
        "                \"HTTPS client behaviour in this engine version. The HTTP step families \" +\n" +
        "                \"(http.rest, http.soap, metrics-assert.prometheus) implement 'tls' and 'mtls'.\");\n" +
        "        }\n" +
        "\n" +
        "        // The trust-anchor half is identical under BOTH wired profiles: 'tls' and 'mtls'\n" +
        "        // differ only in whether a client identity is presented, never in how the peer is\n" +
        "        // judged.\n" +
        "        if (certificates != null)\n" +
        "        {\n" +
        "            var caCertificate = certificates.CaCertificate;\n" +
        "            if (caCertificate != null)\n" +
        "            {\n" +
        "                // Set ONLY when a CA is actually declared. Installing a callback\n" +
        "                // unconditionally would replace the platform's own verdict with this\n" +
        "                // engine's for every request, including the ones that never asked for a\n" +
        "                // private trust anchor.\n" +
        "                //\n" +
        "                // A declared caCert is a PIN: the callback runs on EVERY outcome, including\n" +
        "                // SslPolicyErrors.None, so a certificate the machine store accepts but that\n" +
        "                // does not chain to the declared anchor is rejected.\n" +
        "                //\n" +
        "                // `chain` is forwarded rather than discarded: it is the chain the PLATFORM\n" +
        "                // built for this handshake, which carries any intermediate the peer sent —\n" +
        "                // what a two-tier PKI (offline root declared as caCert, issuing intermediate\n" +
        "                // sent by the server) needs for path building. The accessor treats every\n" +
        "                // element as an untrusted candidate link, never as an anchor.\n" +
        "                handler.ServerCertificateCustomValidationCallback =\n" +
        "                    (request, remoteCertificate, chain, sslPolicyErrors) =>\n" +
        "                        certificates.TrustsRemoteCertificate(remoteCertificate, chain, sslPolicyErrors);\n" +
        "            }\n" +
        "        }\n" +
        "\n" +
        "        // The certificate objects are BORROWED, never disposed here: the accessor owns\n" +
        "        // them for the scenario and hands the same instances to the next step that\n" +
        "        // resolves the same target.\n" +
        "    }\n" +
        "}\n";
}
