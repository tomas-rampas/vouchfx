static class KafkaSecurity_Helpers
{
    /// <summary>
    /// Sets transport security on <paramref name="config"/> from the client security
    /// configuration declared for <paramref name="targetName"/> (REQ-015). A target with
    /// no declared security leaves the configuration untouched, so an unsecured suite
    /// emits exactly the plaintext client it always did.
    /// </summary>
    public static void ConfigureClient(
        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,
        string targetName,
        Confluent.Kafka.ClientConfig config)
    {
        var configuration = security?.For(targetName);
        if (configuration == null)
        {
            // No security declared for this target. Configure NOTHING — not even a
            // SecurityProtocol — so the emitted client is byte-for-byte the pre-feature
            // plaintext one (REQ-001: an absent security block is a normal configuration,
            // never a defaulted one).
            return;
        }

        // WHAT PRESENTS A CLIENT IDENTITY IS THE DECLARED PROFILE, NEVER WHICH FIELDS
        // HAPPEN TO BE NON-NULL. `profile` is the MECHANISM SELECTOR (the same role
        // `type: family.provider` plays for a step), so the mapping from profile to Kafka
        // transport semantics is written out here, exhaustively, and anything absent from
        // it FAILS CLOSED.
        //
        // Why that matters even though the engine's SecurityProfileRegistry already refuses
        // an unregistered profile before a suite reaches here: REQ-019 makes the
        // discriminator OPEN, so a LATER profile — SASL/SCRAM, Kerberos, OAuth bearer, each
        // of which Kafka reaches through a DIFFERENT SecurityProtocol value than Ssl —
        // would, under a which-fields-are-set test, silently acquire mutual-TLS semantics
        // nobody decided it should have, and connect with the wrong protocol.
        //
        // A FUTURE PROFILE AUTHOR MUST EDIT THIS SWITCH. Registering a wiring makes a
        // profile declarable; it does not decide what a Kafka step does with it. The
        // profiles named here are pinned equal to the registry's KAFKA-wired set by
        // SecurityProfileRegistryTests, so a profile added to the registry alone turns the
        // suite red rather than inheriting these semantics by accident.
        var profile = configuration.Profile;
        var certificates = configuration.Certificates;

        if (string.Equals(profile, "mtls", System.StringComparison.Ordinal))
        {
            // Reading the PATH view re-checks containment (REQ-003) on every read, so a
            // declared path that escapes the suite directory throws here, inside the
            // caller's guarded region, and becomes a step-scoped EnvironmentError naming
            // the declared (relative) path — never an opaque librdkafka failure.
            var clientCertificatePath = certificates?.ClientCertificatePath;
            var clientKeyPath = certificates?.ClientKeyPath;
            // THE PASSPHRASE IS READ UNCONDITIONALLY, AND READ HERE (client-key-password,
            // REQ-008). Those are two claims of DIFFERENT WEIGHT, and collapsing them into
            // one word would misprice the second.
            //
            // The first is a SAFETY property: whenever this branch hands librdkafka a
            // client key, the accessor's checks on the declared passphrase have run. It
            // holds on every path an author can reach and is what the gate test pins.
            //
            // The second is a DIAGNOSTIC PREFERENCE: it decides which of two
            // SecurityMaterialException messages an embedder sees on a path the schema
            // already refuses. Getting it wrong costs a worse message, never a weaker
            // connection.
            //
            // UNCONDITIONAL, because on the production material this property is where the
            // checking happens. ClientKeyPassword is lazy: the refusal of a passphrase
            // declared against a key that is not actually encrypted, the refusal of one
            // declared with no clientKey, and the fail-closed handling of an unresolvable or
            // empty reference ALL run on the first read of this property, and reading
            // ClientKeyPath does not trigger any of them. A helper that read only the paths,
            // or that hid this read behind a test on something else, would hand librdkafka a
            // key while leaving every one of those controls unexecuted.
            //
            // HERE — ahead of the client-identity guard below — because the accessor's own
            // 'passphrase declared without a matching clientKey' diagnostic names the field
            // and says what to do about it, where the generic 'requires a client identity'
            // message would only report the symptom. The two path reads still come first, so
            // an escaping path keeps the containment diagnostic it has today.
            //
            // Reveal() is the deliberate, greppable audit point: librdkafka takes a
            // passphrase only as characters, so the value is unwrapped and goes straight
            // into the client configuration below — never into a diagnostic, a Vars key or
            // anything that outlives this call.
            var clientKeyPassword = certificates?.ClientKeyPassword?.Reveal();
            if (clientCertificatePath == null || clientKeyPath == null)
            {
                // Declared mutual TLS, nothing to present. Continuing would set
                // SecurityProtocol.Ssl and connect with NO client identity, and against a
                // broker whose listener requests but does not require one that is a green
                // suite which authenticated nothing.
                throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(
                    "target '" + targetName + "': profile 'mtls' requires a client identity, but no " +
                    "'clientCert'/'clientKey' pair resolved for this target. Declare both, or use " +
                    "'profile: tls' to present no client identity.");
            }

            config.SecurityProtocol = Confluent.Kafka.SecurityProtocol.Ssl;
            config.SslCertificateLocation = clientCertificatePath;
            config.SslKeyLocation = clientKeyPath;
            // ASSIGNED ONLY WHEN NON-NULL, the same shape as the ssl.ca.location guard
            // below and resting on the same MEASURED fact about the pinned
            // Confluent.Kafka 2.14.2: ClientConfig is a keyed property bag, assigning null
            // REMOVES 'ssl.key.password' while assigning "" ADDS it with an empty value.
            // Those are different configurations. Guarding the assignment rather than
            // assigning a possibly-null value and relying on the removal behaviour keeps
            // the intent legible and does not depend on that library detail staying true.
            //
            // What librdkafka would do with an empty ssl.key.password is NOT measured
            // here — it is a question about that library's config parser and OpenSSL, not
            // about this engine — and nothing in this helper depends on the answer.
            //
            // '!= null' rather than string.IsNullOrEmpty, deliberately, and the reason is
            // about WHO OWNS THE RULE rather than about which values reach here. An empty
            // RESOLVED value is refused by the engine accessor with a diagnostic that can
            // say why (the empty-value guard in SecurityConfigurationAccessor's
            // ResolveClientKeyPassword — cited by METHOD because this literal is a public
            // const that INLINES into every consuming assembly, so a line number here goes
            // stale inside shipped IL that no rebuild of this project can correct) — but
            // that guard covers ONE IMPLEMENTATION, not this interface.
            // ISecurityCertificateMaterial's
            // ClientKeyPassword is a default-implemented public v1 member, so material
            // supplied by a direct engine embedder can hand this seam an empty
            // SecretString and arrive outside that refusal, exactly the bypass the 'tls'
            // arm below names. Re-spelling the rule here would still not fix that: a
            // second copy could only drift from the one that owns it, and could report
            // nothing an author could act on. Null keeps one meaning — no passphrase was
            // declared — and the empty case stays the accessor's to diagnose.
            if (clientKeyPassword != null)
            {
                config.SslKeyPassword = clientKeyPassword;
            }
        }
        else if (string.Equals(profile, "tls", System.StringComparison.Ordinal))
        {
            // 'tls' means SERVER authentication only. Client material declared alongside it
            // is a contradiction, not a hint: the schema forbids the combination outright,
            // so reaching here means an embedder bypassed it, and the two readings —
            // 'they meant mtls' and 'they meant no client identity' — differ by exactly
            // whether the run authenticates. Refuse rather than pick.
            if (certificates != null &&
                (certificates.ClientCertificatePath != null || certificates.ClientKeyPath != null))
            {
                throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(
                    "target '" + targetName + "': profile 'tls' presents no client identity, but " +
                    "'clientCert'/'clientKey' is declared for it. Use 'profile: mtls' to present the " +
                    "declared client certificate, or remove both fields.");
            }

            // NO PASSPHRASE READ HERE, AND THAT IS A DECISION, not an omission — do not
            // 'fix' it in isolation. REQ-008 scopes its unconditional-read constraint to
            // the 'mtls' branch, because the read exists to guard a key this branch never
            // presents. The schema forbids 'clientKeyPassword' under 'tls' outright
            // (the boolean-'false' subschema in the 'security' $def's own allOf, in
            // root-language-schema.json), so the only way to declare one here is the
            // same embedder bypass the contradiction guard above addresses, and the
            // consequence is a missing diagnostic rather than a weakened connection:
            // nothing is decrypted, nothing is presented, no key reaches librdkafka.
            // Refusing the declaration belongs where declarations are refused, not in a
            // read performed for its side effect.
            config.SecurityProtocol = Confluent.Kafka.SecurityProtocol.Ssl;
        }
        else
        {
            throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(
                "target '" + targetName + "': security profile '" + profile + "' has no defined " +
                "Kafka client behaviour in this engine version. The Kafka step families " +
                "(mq-publish.kafka, mq-expect.kafka) implement 'tls' and 'mtls'.");
        }

        // The trust-anchor half is identical under BOTH wired profiles: 'tls' and 'mtls'
        // differ only in whether a client identity is presented, never in how the broker is
        // judged.
        //
        // ASSIGNED ONLY WHEN DECLARED (REQ-001, REQ-015). ClientConfig is a keyed property
        // bag: assigning null REMOVES 'ssl.ca.location', assigning "" ADDS it with an empty
        // value — and an empty path is a file librdkafka tries to open and fails on, not a
        // fallback. Guarding the assignment (rather than assigning a possibly-null value and
        // relying on the removal behaviour) keeps the intent legible and does not depend on
        // that library detail staying true.
        var caCertificatePath = certificates?.CaCertificatePath;
        if (caCertificatePath != null)
        {
            config.SslCaLocation = caCertificatePath;
        }
    }
}
