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
// REQ-014's accessor expose both views. Of the two CERTIFICATE views this helper therefore reads
// only the path one, and reads it through the interface's own properties so the containment
// re-check those getters perform (REQ-003, defence in depth) still applies.
//
// The one non-path thing it reads is the client-key PASSPHRASE (client-key-password, REQ-008), for
// the same reason inverted: librdkafka takes a passphrase only as characters, so there is no object
// form to hand it instead. Within the 'mtls' branch — the only branch that presents a client key
// at all — that read is unconditional, and its siting is constrained. See the comments at the read
// itself, and the note in the 'tls' arm recording why no equivalent read belongs there.
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
    /// <para>
    /// Paste this constant as a single element of
    /// <see cref="CsxFragment.RequiredHelpers"/>; the assembler deduplicates it across every
    /// step in the suite. Only providers that also contribute the <c>Confluent.Kafka</c> assembly
    /// through <see cref="ICompileReferenceContributor"/> may splice it.
    /// </para>
    /// <para>
    /// <strong>PATH DISCLOSURE: the leak this helper can cause is closed AT THE SINK, not here
    /// (issue #375).</strong> This helper hands librdkafka the RESOLVED absolute paths —
    /// <c>ssl.ca.location</c>, <c>ssl.certificate.location</c>, <c>ssl.key.location</c> take
    /// nothing else (REQ-015) — and when librdkafka cannot open one, its own error text names the
    /// path it tried. That text becomes a step observation and is archived into the §14 event
    /// stream. The engine's own diagnostics on this path always complied
    /// (<c>SecurityMaterialException</c> names the declared text); this is the library's text, not
    /// the engine's, and no code written at THIS seam could constrain it.
    /// </para>
    /// <para>
    /// What closes it is <c>Vouchfx.Engine.Orchestration.SecurityPathDisclosureLedger</c>: the
    /// engine records (resolved path → the author's declared text) at each chokepoint that hands
    /// the resolved form to code the engine does not write, and substitutes the declared form back
    /// at the three scrub chokepoints every archived channel already passes through. Nothing about
    /// this helper changed, and nothing about it needed to — which is the point of the shape that
    /// was chosen. (The ledger was born in
    /// <c>Vouchfx.Engine.Runtime</c> and #473 lifted it DOWN into Orchestration, because the
    /// sibling recording sites live there and Runtime references Orchestration rather than the
    /// reverse. The namespace here is corrected rather than the type renamed.)
    /// </para>
    /// <para>
    /// <strong>WHAT THE LEDGER NOW COVERS, and where it still does not reach.</strong> #375
    /// recorded exactly <c>caCert</c>/<c>clientCert</c>/<c>clientKey</c>, at the accessor
    /// chokepoint. #473 added the two sibling sites that hold both halves and discarded the
    /// declared one: <c>security.serverArtifacts[].source</c>, which sits in the very same
    /// <c>security:</c> block and is handed to Aspire's container-file staging, and each resolved
    /// <c>environment.seed</c> SQL path, whose diagnostics splice a BCL or driver message the
    /// engine did not write.
    /// </para>
    /// <para>
    /// The ledger substitutes into text the ENGINE DID NOT WRITE, and that is the boundary of what
    /// it is for. Every engine-owned diagnostic names the declared path by construction instead
    /// (#357) — <c>SeedFixtures</c>' fixture-not-found throw and <c>script.csharp</c>'s
    /// <c>file:</c>-not-found refusal are the two #473 examined and left on that side of the line,
    /// the latter also being structurally unable to reach the ledger at all, since a provider
    /// references only <c>Vouchfx.Sdk</c> and <c>Vouchfx.Engine.Abstractions</c>. A path that
    /// reaches a diagnostic through neither route is still invisible to both mechanisms; that is
    /// the ledger's stated limit, not an open ticket.
    /// </para>
    /// <para>
    /// The two remediations considered at THIS seam were measured and REJECTED, and the reasoning
    /// is kept because it is what argues for the sink:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <strong>Register the three resolved paths with the existing secret-scrub set.</strong>
    ///     Not reachable from this seam, and wrong even where it is reachable. The ledger is
    ///     <c>SecretAccessor.ResolvedSecrets</c>, a member of the CONCRETE class;
    ///     <c>ISecretAccessor</c> declares exactly one member (<c>Resolve</c>), and the emitted
    ///     script holds only the interface, through <c>ScriptGlobalVariables.Secrets</c>. Closing
    ///     it this way means adding a member to a public v1 interface that the emitted CSX names
    ///     by type. Separately: that ledger's substitution is the generic
    ///     <c>SecretString.RedactedMarker</c>, and a path blanked to <c>[REDACTED]</c> is worse
    ///     for the author than the leak is for the host.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>Catch <c>KafkaException</c> and rewrite the message to the declared path.</strong>
    ///     Two obstacles. The catch cannot live in <c>ConfigureClient</c>, which only SETS
    ///     configuration and never opens a file — librdkafka opens it when the producer or consumer
    ///     is built, inside each provider's own emitted body (four sites across the two providers:
    ///     plain and Avro, publish and expect). And there is nothing to rewrite TO:
    ///     <c>ISecurityCertificateMaterial</c> exposes the RESOLVED path views only, with no
    ///     declared-text view, so the best available substitution is the field name
    ///     (<c>'caCert'</c>) — a different diagnostic rather than a redaction of the same one.
    ///     The engine-side ledger has the declared text because it sits where that text lives.
    ///   </description></item>
    /// </list>
    /// <para>
    /// One measurement worth carrying forward, and it is the opposite of what an earlier draft of
    /// this paragraph said. The SIGNATURE golden records this member as
    /// <c>field const System.String Source</c> and does NOT pin the literal value, so editing this
    /// string's content does not trip THAT golden — but the companion helper-sources golden
    /// (<c>vouchfx-sdk-helper-sources.v1.txt</c>, added by #361) SHA-256s the constant's runtime
    /// value through <c>BuildHelperSourceSignature</c>. A body edit is therefore a deliberate
    /// golden change, and it carries a cross-version compatibility cost: a <c>const</c> inlines
    /// into every consuming assembly, and <c>CsxAssembler</c> refuses two same-named helper
    /// classes whose source text differs, so an out-of-tree provider built against an older SDK
    /// breaks in any suite that also splices this helper from an in-tree one.
    /// </para>
    /// <para>
    /// Editing this REMARKS block is free by comparison: it sits outside the constant altogether,
    /// so neither golden can see it. That was re-verified when this correction was written —
    /// <c>SdkContractFreezeTests</c> stayed green with no regeneration.
    /// </para>
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
        "            // NO PASSPHRASE READ HERE, AND THAT IS A DECISION, not an omission — do not\n" +
        "            // 'fix' it in isolation. REQ-008 scopes its unconditional-read constraint to\n" +
        "            // the 'mtls' branch, because the read exists to guard a key this branch never\n" +
        "            // presents. The schema forbids 'clientKeyPassword' under 'tls' outright\n" +
        "            // (the boolean-'false' subschema in the 'security' $def's own allOf, in\n" +
        "            // root-language-schema.json), so the only way to declare one here is the\n" +
        "            // same embedder bypass the contradiction guard above addresses, and the\n" +
        "            // consequence is a missing diagnostic rather than a weakened connection:\n" +
        "            // nothing is decrypted, nothing is presented, no key reaches librdkafka.\n" +
        "            // Refusing the declaration belongs where declarations are refused, not in a\n" +
        "            // read performed for its side effect.\n" +
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
