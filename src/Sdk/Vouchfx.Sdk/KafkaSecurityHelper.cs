// Vouchfx.Sdk — KafkaSecurityHelper (authenticated-infrastructure-mtls, slice E — REQ-015).
//
// Supplies the compile-time constant source for the KafkaSecurity_Helpers static class that
// mq-publish.kafka and mq-expect.kafka splice into their RequiredHelpers set — the Kafka
// counterpart of slice D's SecurityHelper (which does the same job for the HTTP family's
// HttpClientHandler). Deduplication is handled by the CsxAssembler helper-dedup logic
// (§13.3.1), so a suite whose steps span both Kafka providers carries ONE copy.
//
// WHY KAFKA NEEDS PROVIDER CODE AT ALL, when eleven other kinds do not (REQ-015). Confluent.Kafka's
// ProducerConfig/ConsumerConfig do NOT derive SecurityProtocol from the bootstrap-servers string
// the way RabbitMQ derives it from an `amqps://` scheme, NATS from `tls://`, Redis from
// `,ssl=true` or MongoDB from `tls=true`. There is no connection-string channel to carry it, so
// the transport decision has to be made in the emitted client configuration. This file is that
// decision, written once.
//
// WHY A SEPARATE CLASS FROM SecurityHelper rather than a second member on it. The two helper
// sources are spliced by DISJOINT provider sets and reference disjoint types: this one names
// Confluent.Kafka.ClientConfig, which resolves only because the two Kafka providers contribute the
// Confluent.Kafka assembly through ICompileReferenceContributor. Splicing it into an HTTP-only
// suite would not compile. Keeping them separate keeps each splice decision with the provider that
// can honour it.
//
// WHY PATHS, NOT CERTIFICATE OBJECTS. librdkafka accepts FILE PATHS for its trust anchor, client
// certificate and client key and never an X509Certificate2 — the exact asymmetry that made
// REQ-014's accessor expose both views. This helper therefore reads ONLY
// ISecurityCertificateMaterial's path view, and reads it through the interface's own properties so
// the containment re-check those getters perform (REQ-003, defence in depth) still applies.
//
// Design constraints (§13.3.1), identical to SecretHelper's and SecurityHelper's:
//   • Static class prefixed 'KafkaSecurity_' so it cannot collide with a provider-specific helper.
//   • Every type fully-qualified — the helper compiles independently of 'using' ordering.
//   • No 'using var' — prohibited in a Roslyn script body.
//   • Byte-identical across providers so CsxAssembler dedupes it to one copy.
//   • No per-step interpolation: everything that varies is a runtime argument.
namespace Vouchfx.Sdk;

/// <summary>
/// Supplies the canonical source text for the <c>KafkaSecurity_Helpers</c> static class the
/// <c>mq-publish.kafka</c> and <c>mq-expect.kafka</c> providers splice into their
/// <see cref="CsxFragment.RequiredHelpers"/> to set transport security on the emitted
/// <c>ProducerConfig</c>/<c>ConsumerConfig</c> from the client security configuration declared
/// for a step's own <c>target</c> (authenticated-infrastructure-mtls, REQ-015).
/// </summary>
/// <remarks>
/// <para>
/// The material is read at STEP-EXECUTION time through <c>ScriptGlobalVariables.Security</c>
/// (REQ-014), never at compile time: baking a certificate path into the emitted script would
/// defeat compile-once and corrupt the reproducibility envelope, the same rule §17 already
/// imposes on secrets.
/// </para>
/// <para>
/// <strong>An absent <c>caCert</c> leaves <c>SslCaLocation</c> UNSET — never empty, never
/// defaulted (REQ-001, REQ-015).</strong> Measured against the pinned Confluent.Kafka 2.14.2:
/// <c>ClientConfig</c> is a keyed property bag, and assigning <see langword="null"/> to
/// <c>SslCaLocation</c> REMOVES the <c>ssl.ca.location</c> key, whereas assigning
/// <c>""</c> ADDS it with an empty value. Those are different configurations — an empty
/// <c>ssl.ca.location</c> is a path librdkafka will try to open and fail on, not a fallback — so
/// this helper assigns the property only when a path was actually declared, and the key is
/// genuinely absent from the emitted client's configuration otherwise. librdkafka then falls back
/// to whatever trust material the declared client material already carries, or to its own default
/// (system) trust store resolution.
/// </para>
/// <para>
/// <strong>The declared profile decides what happens, and an unknown profile fails closed.</strong>
/// <c>mtls</c> presents the declared client identity <em>and requires one</em>; <c>tls</c> presents
/// none and refuses contradictory <c>clientCert</c>/<c>clientKey</c>; any other profile throws
/// <c>SecurityMaterialException</c>. Because REQ-019 makes the profile discriminator OPEN,
/// inferring the behaviour from which certificate fields happen to be non-null would let a later
/// profile inherit Kafka client-certificate semantics nobody chose for it.
/// </para>
/// </remarks>
public static class KafkaSecurityHelper
{
    /// <summary>
    /// The full C# source text of the <c>KafkaSecurity_Helpers</c> static class.
    /// </summary>
    /// <remarks>
    /// Paste this constant as a single element of
    /// <see cref="CsxFragment.RequiredHelpers"/>; the assembler deduplicates it across every
    /// step in the suite. Only providers that also contribute the <c>Confluent.Kafka</c> assembly
    /// through <see cref="ICompileReferenceContributor"/> may splice it.
    /// </remarks>
    public const string Source =
        "static class KafkaSecurity_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Sets transport security on <paramref name=\"config\"/> from the client security\n" +
        "    /// configuration declared for <paramref name=\"targetName\"/> (REQ-015). A target with\n" +
        "    /// no declared security leaves the configuration untouched, so an unsecured suite\n" +
        "    /// emits exactly the plaintext client it always did.\n" +
        "    /// </summary>\n" +
        "    public static void ConfigureClient(\n" +
        "        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,\n" +
        "        string targetName,\n" +
        "        Confluent.Kafka.ClientConfig config)\n" +
        "    {\n" +
        "        var configuration = security?.For(targetName);\n" +
        "        if (configuration == null)\n" +
        "        {\n" +
        "            // No security declared for this target. Configure NOTHING — not even a\n" +
        "            // SecurityProtocol — so the emitted client is byte-for-byte the pre-feature\n" +
        "            // plaintext one (REQ-001: an absent security block is a normal configuration,\n" +
        "            // never a defaulted one).\n" +
        "            return;\n" +
        "        }\n" +
        "\n" +
        "        // WHAT PRESENTS A CLIENT IDENTITY IS THE DECLARED PROFILE, NEVER WHICH FIELDS\n" +
        "        // HAPPEN TO BE NON-NULL. `profile` is the MECHANISM SELECTOR (the same role\n" +
        "        // `type: family.provider` plays for a step), so the mapping from profile to Kafka\n" +
        "        // transport semantics is written out here, exhaustively, and anything absent from\n" +
        "        // it FAILS CLOSED.\n" +
        "        //\n" +
        "        // Why that matters even though the engine's SecurityProfileRegistry already refuses\n" +
        "        // an unregistered profile before a suite reaches here: REQ-019 makes the\n" +
        "        // discriminator OPEN, so a LATER profile — SASL/SCRAM, Kerberos, OAuth bearer, each\n" +
        "        // of which Kafka reaches through a DIFFERENT SecurityProtocol value than Ssl —\n" +
        "        // would, under a which-fields-are-set test, silently acquire mutual-TLS semantics\n" +
        "        // nobody decided it should have, and connect with the wrong protocol.\n" +
        "        //\n" +
        "        // A FUTURE PROFILE AUTHOR MUST EDIT THIS SWITCH. Registering a wiring makes a\n" +
        "        // profile declarable; it does not decide what a Kafka step does with it. The\n" +
        "        // profiles named here are pinned equal to the registry's KAFKA-wired set by\n" +
        "        // SecurityProfileRegistryTests, so a profile added to the registry alone turns the\n" +
        "        // suite red rather than inheriting these semantics by accident.\n" +
        "        var profile = configuration.Profile;\n" +
        "        var certificates = configuration.Certificates;\n" +
        "\n" +
        "        if (string.Equals(profile, \"mtls\", System.StringComparison.Ordinal))\n" +
        "        {\n" +
        "            // Reading the PATH view re-checks containment (REQ-003) on every read, so a\n" +
        "            // declared path that escapes the suite directory throws here, inside the\n" +
        "            // caller's guarded region, and becomes a step-scoped EnvironmentError naming\n" +
        "            // the declared (relative) path — never an opaque librdkafka failure.\n" +
        "            var clientCertificatePath = certificates?.ClientCertificatePath;\n" +
        "            var clientKeyPath = certificates?.ClientKeyPath;\n" +
        "            if (clientCertificatePath == null || clientKeyPath == null)\n" +
        "            {\n" +
        "                // Declared mutual TLS, nothing to present. Continuing would set\n" +
        "                // SecurityProtocol.Ssl and connect with NO client identity, and against a\n" +
        "                // broker whose listener requests but does not require one that is a green\n" +
        "                // suite which authenticated nothing.\n" +
        "                throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(\n" +
        "                    \"target '\" + targetName + \"': profile 'mtls' requires a client identity, but no \" +\n" +
        "                    \"'clientCert'/'clientKey' pair resolved for this target. Declare both, or use \" +\n" +
        "                    \"'profile: tls' to present no client identity.\");\n" +
        "            }\n" +
        "\n" +
        "            config.SecurityProtocol = Confluent.Kafka.SecurityProtocol.Ssl;\n" +
        "            config.SslCertificateLocation = clientCertificatePath;\n" +
        "            config.SslKeyLocation = clientKeyPath;\n" +
        "        }\n" +
        "        else if (string.Equals(profile, \"tls\", System.StringComparison.Ordinal))\n" +
        "        {\n" +
        "            // 'tls' means SERVER authentication only. Client material declared alongside it\n" +
        "            // is a contradiction, not a hint: the schema forbids the combination outright,\n" +
        "            // so reaching here means an embedder bypassed it, and the two readings —\n" +
        "            // 'they meant mtls' and 'they meant no client identity' — differ by exactly\n" +
        "            // whether the run authenticates. Refuse rather than pick.\n" +
        "            if (certificates != null &&\n" +
        "                (certificates.ClientCertificatePath != null || certificates.ClientKeyPath != null))\n" +
        "            {\n" +
        "                throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(\n" +
        "                    \"target '\" + targetName + \"': profile 'tls' presents no client identity, but \" +\n" +
        "                    \"'clientCert'/'clientKey' is declared for it. Use 'profile: mtls' to present the \" +\n" +
        "                    \"declared client certificate, or remove both fields.\");\n" +
        "            }\n" +
        "\n" +
        "            config.SecurityProtocol = Confluent.Kafka.SecurityProtocol.Ssl;\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(\n" +
        "                \"target '\" + targetName + \"': security profile '\" + profile + \"' has no defined \" +\n" +
        "                \"Kafka client behaviour in this engine version. The Kafka step families \" +\n" +
        "                \"(mq-publish.kafka, mq-expect.kafka) implement 'tls' and 'mtls'.\");\n" +
        "        }\n" +
        "\n" +
        "        // The trust-anchor half is identical under BOTH wired profiles: 'tls' and 'mtls'\n" +
        "        // differ only in whether a client identity is presented, never in how the broker is\n" +
        "        // judged.\n" +
        "        //\n" +
        "        // ASSIGNED ONLY WHEN DECLARED (REQ-001, REQ-015). ClientConfig is a keyed property\n" +
        "        // bag: assigning null REMOVES 'ssl.ca.location', assigning \"\" ADDS it with an empty\n" +
        "        // value — and an empty path is a file librdkafka tries to open and fails on, not a\n" +
        "        // fallback. Guarding the assignment (rather than assigning a possibly-null value and\n" +
        "        // relying on the removal behaviour) keeps the intent legible and does not depend on\n" +
        "        // that library detail staying true.\n" +
        "        var caCertificatePath = certificates?.CaCertificatePath;\n" +
        "        if (caCertificatePath != null)\n" +
        "        {\n" +
        "            config.SslCaLocation = caCertificatePath;\n" +
        "        }\n" +
        "    }\n" +
        "}\n";
}
