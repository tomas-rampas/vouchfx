// Vouchfx.Sdk — SecurityHelper (authenticated-infrastructure-mtls, slice D — REQ-024).
//
// Provides the compile-time constant source for the Security_Helpers static class that the
// HTTP-family providers splice into their RequiredHelpers set, exactly mirroring
// SecretHelper (S05-B-02) and SubstituteHelper (S04-B-03). Deduplication is handled by the
// CsxAssembler helper-dedup logic (§13.3.1), so a suite whose steps span http.rest,
// http.soap and metrics-assert.prometheus carries ONE copy of this class, not three.
//
// Why a shared helper rather than three copies. All three providers need the same ~20 lines:
// present the declared client certificate, and rebuild the server chain against the declared
// CA. Three copies of a transport-security decision are three places for one of them to be
// subtly weakened — the failure mode that matters here is a copy that forgives a name
// mismatch, or one that sets a custom validation callback even when no CA was declared and
// so silently replaces the platform's own verdict. One source removes the possibility.
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
        "    /// client certificate, and trusts the declared CA as a custom root. A target with\n" +
        "    /// no declared security leaves the handler untouched.\n" +
        "    /// </summary>\n" +
        "    public static void ConfigureHandler(\n" +
        "        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,\n" +
        "        string targetName,\n" +
        "        System.Net.Http.HttpClientHandler handler)\n" +
        "    {\n" +
        "        var certificates = security?.For(targetName)?.Certificates;\n" +
        "        if (certificates == null)\n" +
        "        {\n" +
        "            // No security declared for this target, or a profile carrying no certificate\n" +
        "            // material at all. Configure NOTHING: the platform's own trust store applies\n" +
        "            // and no client identity is presented (REQ-001/REQ-024 — an absent caCert is\n" +
        "            // a normal configuration, never a defaulted or synthesised one).\n" +
        "            return;\n" +
        "        }\n" +
        "\n" +
        "        // Reading these properties LOADS the declared files. Both reads happen here,\n" +
        "        // inside the caller's guarded region, so a file that exists (already checked by\n" +
        "        // EnvironmentSecurityValidator pre-topology) but is malformed surfaces as a\n" +
        "        // step-scoped EnvironmentError naming the field, rather than as an opaque\n" +
        "        // handshake failure raised from inside a TLS validation callback.\n" +
        "        var clientCertificate = certificates.ClientCertificate;\n" +
        "        if (clientCertificate != null)\n" +
        "        {\n" +
        "            handler.ClientCertificateOptions = System.Net.Http.ClientCertificateOption.Manual;\n" +
        "            handler.ClientCertificates.Add(clientCertificate);\n" +
        "        }\n" +
        "\n" +
        "        var caCertificate = certificates.CaCertificate;\n" +
        "        if (caCertificate != null)\n" +
        "        {\n" +
        "            // Set ONLY when a CA is actually declared. Installing a callback\n" +
        "            // unconditionally would replace the platform's own verdict with this\n" +
        "            // engine's for every request, including the ones that never asked for a\n" +
        "            // private trust anchor.\n" +
        "            //\n" +
        "            // A declared caCert is a PIN: the callback runs on EVERY outcome, including\n" +
        "            // SslPolicyErrors.None, so a certificate the machine store accepts but that\n" +
        "            // does not chain to the declared anchor is rejected.\n" +
        "            //\n" +
        "            // `chain` is forwarded rather than discarded: it carries the intermediates\n" +
        "            // the PEER sent, which a two-tier PKI (offline root declared as caCert,\n" +
        "            // issuing intermediate sent by the server) needs for path building. The\n" +
        "            // accessor treats them as untrusted candidate links, never as anchors.\n" +
        "            handler.ServerCertificateCustomValidationCallback =\n" +
        "                (request, remoteCertificate, chain, sslPolicyErrors) =>\n" +
        "                    certificates.TrustsRemoteCertificate(remoteCertificate, chain, sslPolicyErrors);\n" +
        "        }\n" +
        "\n" +
        "        // The certificate objects are BORROWED, never disposed here: the accessor owns\n" +
        "        // them for the scenario and hands the same instances to the next step that\n" +
        "        // resolves the same target.\n" +
        "    }\n" +
        "}\n";
}
