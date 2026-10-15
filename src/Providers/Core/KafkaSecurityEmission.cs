namespace Vouchfx.Steps.Kafka;

// Linked privately into both Core Kafka providers; this adds no SDK public surface.
// Once shipped, this source and its emitted class identity are immutable. A behavior change
// requires a new identity so providers carrying older generations can coexist in one suite.
internal static class KafkaSecurityEmission
{
    internal static string Source => Version2Source;

    private const string Version2Source =
        "static class KafkaSecurity_V2_Helpers\n" +
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
        "            // THE PASSPHRASE IS READ UNCONDITIONALLY, AND READ HERE (client-key-password,\n" +
        "            // REQ-008). Those are two claims of DIFFERENT WEIGHT, and collapsing them into\n" +
        "            // one word would misprice the second.\n" +
        "            //\n" +
        "            // The first is a SAFETY property: whenever this branch hands librdkafka a\n" +
        "            // client key, the accessor's checks on the declared passphrase have run. It\n" +
        "            // holds on every path an author can reach and is what the gate test pins.\n" +
        "            //\n" +
        "            // The second is a DIAGNOSTIC PREFERENCE: it decides which of two\n" +
        "            // SecurityMaterialException messages an embedder sees on a path the schema\n" +
        "            // already refuses. Getting it wrong costs a worse message, never a weaker\n" +
        "            // connection.\n" +
        "            //\n" +
        "            // UNCONDITIONAL, because on the production material this property is where the\n" +
        "            // checking happens. ClientKeyPassword is lazy: the refusal of a passphrase\n" +
        "            // declared against a key that is not actually encrypted, the refusal of one\n" +
        "            // declared with no clientKey, and the fail-closed handling of an unresolvable or\n" +
        "            // empty reference ALL run on the first read of this property, and reading\n" +
        "            // ClientKeyPath does not trigger any of them. A helper that read only the paths,\n" +
        "            // or that hid this read behind a test on something else, would hand librdkafka a\n" +
        "            // key while leaving every one of those controls unexecuted.\n" +
        "            //\n" +
        "            // HERE — ahead of the client-identity guard below — because the accessor's own\n" +
        "            // 'passphrase declared without a matching clientKey' diagnostic names the field\n" +
        "            // and says what to do about it, where the generic 'requires a client identity'\n" +
        "            // message would only report the symptom. The two path reads still come first, so\n" +
        "            // an escaping path keeps the containment diagnostic it has today.\n" +
        "            //\n" +
        "            // Reveal() is the deliberate, greppable audit point: librdkafka takes a\n" +
        "            // passphrase only as characters, so the value is unwrapped and goes straight\n" +
        "            // into the client configuration below — never into a diagnostic, a Vars key or\n" +
        "            // anything that outlives this call.\n" +
        "            var clientKeyPassword = certificates?.ClientKeyPassword?.Reveal();\n" +
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
        "            // ASSIGNED ONLY WHEN NON-NULL, the same shape as the ssl.ca.location guard\n" +
        "            // below and resting on the same MEASURED fact about the pinned\n" +
        "            // Confluent.Kafka 2.14.2: ClientConfig is a keyed property bag, assigning null\n" +
        "            // REMOVES 'ssl.key.password' while assigning \"\" ADDS it with an empty value.\n" +
        "            // Those are different configurations. Guarding the assignment rather than\n" +
        "            // assigning a possibly-null value and relying on the removal behaviour keeps\n" +
        "            // the intent legible and does not depend on that library detail staying true.\n" +
        "            //\n" +
        "            // What librdkafka would do with an empty ssl.key.password is NOT measured\n" +
        "            // here — it is a question about that library's config parser and OpenSSL, not\n" +
        "            // about this engine — and nothing in this helper depends on the answer.\n" +
        "            //\n" +
        "            // '!= null' rather than string.IsNullOrEmpty, deliberately, and the reason is\n" +
        "            // about WHO OWNS THE RULE rather than about which values reach here. An empty\n" +
        "            // RESOLVED value is refused by the engine accessor with a diagnostic that can\n" +
        "            // say why (the empty-value guard in SecurityConfigurationAccessor's\n" +
        "            // ResolveClientKeyPassword — cited by METHOD because this literal is a public\n" +
        "            // const that INLINES into every consuming assembly, so a line number here goes\n" +
        "            // stale inside shipped IL that no rebuild of this project can correct) — but\n" +
        "            // that guard covers ONE IMPLEMENTATION, not this interface.\n" +
        "            // ISecurityCertificateMaterial's\n" +
        "            // ClientKeyPassword is a default-implemented public v1 member, so material\n" +
        "            // supplied by a direct engine embedder can hand this seam an empty\n" +
        "            // SecretString and arrive outside that refusal, exactly the bypass the 'tls'\n" +
        "            // arm below names. Re-spelling the rule here would still not fix that: a\n" +
        "            // second copy could only drift from the one that owns it, and could report\n" +
        "            // nothing an author could act on. Null keeps one meaning — no passphrase was\n" +
        "            // declared — and the empty case stays the accessor's to diagnose.\n" +
        "            if (clientKeyPassword != null)\n" +
        "            {\n" +
        "                config.SslKeyPassword = clientKeyPassword;\n" +
        "            }\n" +
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
        "            // Keep the client-identity contradiction diagnostic above first. The lazy\n" +
        "            // passphrase read also refuses a passphrase-only declaration from an\n" +
        "            // embedder that bypassed schema validation; no client identity is presented.\n" +
        "            _ = certificates?.ClientKeyPassword;\n" +
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
