// Pre-GA schema tightening — close environment.services and type
// environment.dependencies per kind (root-language-schema.json).
//
// Two related narrowings, both deliberate pre-GA closures with no external
// consumers:
//   PART 1 — environment.services gains $defs/service: exactly the five real
//     fields (image, project, imagePullPolicy, httpPort, env). 'image'/'project'
//     mutual exclusivity is enforced by TWO keywords, not a single 'oneOf' —
//     see the schema's own $defs/service description for why 'oneOf' was
//     tried and discarded (JsonSchema.Net 9.2.1 attaches no error message to
//     a failing 'oneOf' node itself). httpPort is bounded to a real TCP port;
//     env values are typed as strings. Before this change a service value had
//     no shape constraint at all beyond "type: object" on the outer map — any
//     key validated, including a misspelled one an upcoming mTLS feature
//     would silently drop (ParseServiceMap reads exactly five keys and has no
//     'Extra' bucket, unlike dependencies — every other key vanishes at parse
//     time and reaches nothing).
//   PART 2 — $defs/dependency gains 'required: ["type"]', 'additionalProperties:
//     false', a closed 'type' enum (the thirteen kinds EnvironmentMapper's own
//     s_dependencyRegistry recognises), and a statically-authored allOf/if/then
//     chain restricting 'schemaRegistry' to kafka and 'queues'/'topics' to
//     azureservicebus — mirroring (but not sharing runtime machinery with)
//     SchemaComposer.BuildIfThenClauses' step-type discriminator pattern. Each
//     'then' forbids its kind's disallowed fields with a per-field boolean
//     'false' subschema, not a 'not'/'required' negation — see the schema's
//     own $defs/dependency description for why.
//
// A NOTE ON MESSAGE SHAPE: a boolean 'false' subschema violation (used both
// for 'additionalProperties: false' — an unknown key — and for the per-field
// forbidden-value checks above) used to be reported by SchemaErrorCollector
// with a BLANK keyword tag and a useless generic message: "[] All values
// fail against the false schema". SchemaErrorCollector now recognises both
// shapes (generalising the treatment already applied to $defs/step's
// unevaluatedProperties: false) and substitutes an actionable message that
// names the offending property and, wherever the InstanceLocation pointer
// resolves it, the property's own service/dependency container — e.g.
// "[additionalProperties] Unknown property 'securty' on service 'app'" or
// "[properties] Property 'schemaRegistry' is not valid on a 'postgres'
// dependency". See SchemaErrorCollector's own remarks and
// FormatAdditionalPropertiesError/FormatForbiddenPropertyError for the full
// shape catalogue and the no-fabrication degrade rule. Because these shapes
// now carry a genuinely non-empty keyword ('additionalProperties' or
// 'properties'), the Corpus/Rejected header-comment convention
// (SchemaRejectedCorpusTests) can pin them too — see
// Corpus/Rejected/service-unknown-key.e2e.yaml,
// service-project-with-image.e2e.yaml, dependency-unknown-key.e2e.yaml,
// dependency-schemaregistry-on-postgres.e2e.yaml and
// topics-item-unknown-key.e2e.yaml — alongside the unit-level pins below,
// which additionally exercise the per-kind business rules (kafka-only,
// azureservicebus-only) the corpus fixtures don't each repeat.
//
// These tests exercise the ROOT schema only (YamlSchemaValidator, no provider
// fragments) — environment.services/dependencies constraints live entirely in
// root-language-schema.json, never in a provider's JsonSchemaFragment. Step
// bodies use bare 'id'/'type' only (no provider-specific fields such as
// script.csharp's 'code') because the root-only schema has no provider allOf
// clause to mark such a field evaluated — see RootSchemaTests's own
// Validate_ProviderSpecificExtraFields_AreRejectedWithoutProviderClauses.
// See SchemaAcceptedCorpusTests / SchemaRejectedCorpusTests for the
// corpus-level safety net (via DocumentValidator, the composed path an
// author's suite actually hits) that these unit tests complement.
using System.Linq;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Root-schema unit tests for the pre-GA <c>environment.services</c> /
/// <c>environment.dependencies</c> tightening.
/// </summary>
public sealed class EnvironmentSchemaTests
{
    // ── Part 1: environment.services ────────────────────────────────────────

    /// <summary>
    /// The exact scenario the brief is protecting against: a misspelled key
    /// under a service (e.g. an upcoming mTLS 'securty:' typo) must now be
    /// rejected, not silently accepted and dropped at parse time — AND, per
    /// the follow-up brief, the rejection must name the offending property
    /// and its containing service rather than the old blank-keyword message.
    /// </summary>
    [Fact]
    public void Service_UnknownKey_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  securty: mtls
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A misspelled/unknown service key must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app/securty" &&
            e.Message.Contains("[additionalProperties] Unknown property 'securty' on service 'app'", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The message must name the actual rule the author broke (two mutually
    /// exclusive fields both set), not merely the offending field — see the
    /// class header's "A NOTE ON MESSAGE SHAPE" remarks.
    /// </summary>
    [Fact]
    public void Service_BothImageAndProject_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  project: ./src/App/App.csproj
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A service with both 'image' and 'project' must be rejected.");
        // Exactly one error (feat/close-remaining-surfaces, Part A pin): both
        // 'anyOf' branches (required:[image], required:[project]) are
        // individually SATISFIED here (both fields are present), so neither
        // contributes noise — only the genuine 'allOf/if/then' mutual-
        // exclusion violation survives.
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/environment/services/app/project", onlyError.InstanceLocation);
        Assert.Contains(
            "[properties] Property 'project' cannot be combined with 'image' on service 'app'",
            onlyError.Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void Service_NeitherImageNorProject_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  httpPort: 8080
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A service with neither 'image' nor 'project' must be rejected.");
        // Exactly two errors (feat/close-remaining-surfaces, Part A pin): the
        // 'anyOf' composite genuinely fails here (NEITHER branch is
        // satisfied), so — unlike the noise this suite elsewhere guards
        // against — both branches' 'required' errors are genuine,
        // independently-reportable defects and must both survive, exactly as
        // $defs/service's own description promises.
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal("/environment/services/app", e.InstanceLocation));
        Assert.Contains(result.Errors, e => e.Message.Contains("\"image\"", System.StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Message.Contains("\"project\"", System.StringComparison.Ordinal));
        Assert.All(result.Errors, e => Assert.Contains("[required]", e.Message, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Service_BareScalarValue_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app: myorg/app:1.0
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A bare-scalar service value must be rejected (unlike a bare-scalar dependency).");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Always")]
    [InlineData("Missing")]
    [InlineData("Never")]
    public void Service_ImagePullPolicyRecognisedValue_IsAccepted(string value)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  imagePullPolicy: {{value}}
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// The engine's own parser (<c>EnvironmentMapper.ParseImagePullPolicy</c>)
    /// accepts these three values case-INSENSITIVELY. The schema is
    /// case-sensitive at BOTH the environment level (pre-existing) and the new
    /// service level (this change) — deliberately kept consistent between the
    /// two, per the brief, rather than silently picking one. This test pins
    /// that a lower-cased value is rejected by the schema even though the
    /// runtime would accept it — the case-sensitivity finding reported
    /// alongside this change.
    /// </summary>
    /// <remarks>
    /// Also pins the <c>[enum]</c> enrichment (feat/close-remaining-surfaces,
    /// Part C): a case-insensitive match exists ('Always'), so the message
    /// must both list the accepted values AND name the correct spelling
    /// directly — the CHANGELOG's "the fix is in the message" promise, made
    /// true at the gate an author actually hits first (schema validation, not
    /// EnvironmentMapper.ParseImagePullPolicy, which never runs on this path).
    /// </remarks>
    [Fact]
    public void Service_ImagePullPolicyLowerCase_IsRejectedByTheSchemaThoughTheEngineWouldAcceptIt()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  imagePullPolicy: always
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A lower-cased imagePullPolicy value must be rejected by the case-sensitive schema enum.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app/imagePullPolicy" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            e.Message.Contains("'always'", System.StringComparison.Ordinal) &&
            e.Message.Contains("Always", System.StringComparison.Ordinal) &&
            e.Message.Contains("Missing", System.StringComparison.Ordinal) &&
            e.Message.Contains("Never", System.StringComparison.Ordinal) &&
            e.Message.Contains("write 'Always'", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Same field, the ENVIRONMENT level rather than per-service — same enum,
    /// same enrichment mechanism, pinned independently since the two live at
    /// different schema locations (<c>$defs/environment/properties/imagePullPolicy</c>
    /// vs <c>$defs/service/properties/imagePullPolicy</c>).
    /// </summary>
    [Fact]
    public void Environment_ImagePullPolicyLowerCase_IsRejectedWithActionableMessage()
    {
        const string yaml = """
            environment:
              imagePullPolicy: never
              services:
                app:
                  image: myorg/app:1.0
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/imagePullPolicy" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            e.Message.Contains("write 'Never'", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The contrastive case: an unrecognised value with NO case-insensitive
    /// match must still list the accepted values, but must NOT fabricate a
    /// "write '...'" suggestion — see <c>FormatEnumError</c>'s no-fabrication
    /// rule.
    /// </summary>
    [Fact]
    public void Service_ImagePullPolicyUnrecognisedValue_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  imagePullPolicy: Sometimes
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app/imagePullPolicy" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            e.Message.Contains("'Sometimes'", System.StringComparison.Ordinal) &&
            e.Message.Contains("Always", System.StringComparison.Ordinal) &&
            !e.Message.Contains("write '", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(65535)]
    public void Service_HttpPortWithinRealPortRange_IsAccepted(int port)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: {{port}}
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// Strengthened (feat/close-remaining-surfaces, Part A1): a bare
    /// out-of-range integer must yield EXACTLY ONE error, carrying
    /// <c>[minimum]</c> or <c>[maximum]</c> — not the old two-branch
    /// <c>oneOf</c>'s spurious second entry (<c>[type] Value is "integer" but
    /// should be "string"</c>, from the string branch, which the JSON value
    /// was never going to match anyway). The original assertion only pinned
    /// location, which stayed green across that exact regression — see the
    /// class header's "A NOTE ON MESSAGE SHAPE" precedent for why this class
    /// pins keyword AND count, not merely location.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(999999)]
    public void Service_HttpPortOutOfRealPortRange_IsRejected(int port)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: {{port}}
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, $"httpPort {port} is out of the real TCP port range and must be rejected.");
        var onlyError = Assert.Single(result.Errors,
            e => e.InstanceLocation == "/environment/services/app/httpPort");
        Assert.True(
            onlyError.Message.Contains("[minimum]", System.StringComparison.Ordinal) ||
            onlyError.Message.Contains("[maximum]", System.StringComparison.Ordinal),
            $"Expected a [minimum]/[maximum] error, got: {onlyError.Message}");
    }

    // ── A1: httpPort/timeout/captureEntry de-branching (composite-branch noise, prong 1) ──
    //
    // The service anyOf/script.csharp oneOf tests above pin PRONG 2 (A2 —
    // SchemaErrorCollector's satisfied-composite-group filter, for composites
    // that genuinely cannot be de-branched into a single type union). These
    // pin PRONG 1: httpPort/timeout/captureEntry's two-branch oneOf/anyOf
    // shapes are REPLACED outright with a single merged schema (verified
    // empirically that minimum/maximum/pattern/required/properties/
    // additionalProperties are all no-ops against a non-matching JSON type,
    // JsonSchema.Net 9.2.1), so there is no second branch left to leak noise
    // from at all — A2's filter alone cannot fix these, because a value that
    // matches NEITHER old branch well (e.g. an out-of-range bare integer
    // against the string branch) makes the OLD two-branch oneOf genuinely
    // invalid as a whole, which is exactly the case A2 must NOT suppress.

    /// <summary>
    /// A perfectly valid, BARE-INTEGER httpPort must never contribute a
    /// spurious "[type] should be string" entry (the old string branch's own
    /// mismatch) merely because the document fails elsewhere.
    /// </summary>
    [Fact]
    public void Service_ValidBareIntegerHttpPort_InDocumentFailingElsewhere_NoSpuriousTypeError()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: 8080
            steps:
              - id: noop
                type: noop.echo
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "The second step is missing 'id' and must still be rejected.");
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation == "/environment/services/app/httpPort");
        Assert.Contains(result.Errors, e => e.InstanceLocation == "/steps/1");
    }

    /// <summary>
    /// Same, for the QUOTED-STRING form: must never advise undoing the
    /// quoting this very schema legalised — the old integer branch's own
    /// type mismatch.
    /// </summary>
    [Fact]
    public void Service_ValidQuotedStringHttpPort_InDocumentFailingElsewhere_NoAdviceToUnquote()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: "8080"
            steps:
              - id: noop
                type: noop.echo
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation == "/environment/services/app/httpPort");
    }

    // ── Composite-branch noise (combined findings, feat/close-remaining-surfaces) ──
    //
    // JsonSchema.Net's List output is FLAT: every node it evaluates — including
    // a non-selected anyOf/oneOf branch's own failing sub-evaluation — is
    // reported as a direct entry, regardless of whether the branch's logical
    // "parent" (the service/step instance the branch belongs to) is itself
    // fully valid. IsIfDiscriminatorNoise (issue #259) already filters this
    // shape for the 'if' keyword; these tests pin the SAME class of noise for
    // 'anyOf'/'oneOf' composites that cannot be de-branched into a single
    // type union (see root-language-schema.json's $defs/service — TWO
    // services sharing the identical EvaluationPath through
    // 'additionalProperties' is exactly why the fix must key on
    // (evaluationPath, instanceLocation) together, not evaluationPath alone).

    /// <summary>
    /// A fully valid, image-only service must never contribute a spurious
    /// "[required] project" entry merely because the document is invalid for
    /// a totally unrelated reason elsewhere (here: a second step missing its
    /// required 'id'). Confirmed by direct execution against the current code
    /// (pre-fix) to leak exactly this: 'loc=/environment/services/app
    /// msg=[required] Required properties ["project"] are not present'
    /// alongside the genuine '/steps/1' error.
    /// </summary>
    [Fact]
    public void Service_ValidImageOnlyService_PlusUnrelatedFailureElsewhere_NoSpuriousProjectError()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
            steps:
              - id: noop
                type: noop.echo
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "The second step is missing 'id' and must still be rejected.");
        Assert.DoesNotContain(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app" ||
            e.InstanceLocation.StartsWith("/environment/services/app/", System.StringComparison.Ordinal));

        // The genuine, unrelated defect must still be reported.
        Assert.Contains(result.Errors, e => e.InstanceLocation == "/steps/1");
    }

    /// <summary>
    /// Two services in the SAME document: one fully valid (image only), one
    /// genuinely broken (neither image nor project). Both share the IDENTICAL
    /// EvaluationPath through 'additionalProperties' (a single schema applied
    /// uniformly to every map value) — only InstanceLocation distinguishes
    /// them. This is the disambiguation case a evaluationPath-only noise
    /// filter would get wrong: it must suppress the valid service's spurious
    /// branch noise WITHOUT ALSO suppressing the broken service's genuine
    /// "neither field set" errors.
    /// </summary>
    [Fact]
    public void Service_OneValidOneGenuinelyBroken_EachJudgedIndependently()
    {
        const string yaml = """
            environment:
              services:
                good:
                  image: myorg/good:1.0
                bad:
                  httpPort: 8080
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "Service 'bad' has neither 'image' nor 'project' and must be rejected.");

        // 'good' contributes nothing at all.
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation.StartsWith("/environment/services/good", System.StringComparison.Ordinal));

        // 'bad' still reports its genuine "neither field set" defect.
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/bad" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A quoted <c>httpPort</c> with leading zeros is accepted, because the parser
    /// reads it as an ordinary integer.
    /// </summary>
    /// <remarks>
    /// <c>YamlDocumentParser</c> reads this field with
    /// <c>int.TryParse(..., NumberStyles.None, ...)</c>, for which leading zeros are
    /// just digits — <c>"08080"</c> yields 8080 and the service is reached on that
    /// port. The first bounded pattern written for this field required a non-zero
    /// leading digit and so rejected it, putting the schema back out of step with the
    /// parser: exactly the mismatch that bounding this field was meant to remove.
    /// Pinned so a future tightening of the range regex cannot quietly reintroduce it.
    /// </remarks>
    [Theory]
    [InlineData("08080")]
    [InlineData("00001")]
    [InlineData("065535")]
    [InlineData("0000000008080")]
    public void Service_QuotedHttpPortWithLeadingZeros_IsAccepted(string port)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: "{{port}}"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected quoted httpPort \"{port}\" to be accepted (int.TryParse reads it as an ordinary " +
            $"integer) but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// Zero remains rejected in every spelling, leading zeros or not: it parses
    /// cleanly, but port 0 is not a port a service can be reached on, so accepting it
    /// would let a silently-unreachable service through.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("0000000")]
    public void Service_QuotedHttpPortZero_IsStillRejected(string port)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: "{{port}}"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, $"httpPort \"{port}\" is port zero and must be rejected.");
        var onlyError = Assert.Single(result.Errors,
            e => e.InstanceLocation == "/environment/services/app/httpPort");
        Assert.Contains("[pattern]", onlyError.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Service_EnvStringValue_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  env:
                    FOO: "bar"
              dependencies:
                db: { type: postgres }
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // ── Part 4b: scalar-coercion widening — env values / httpPort ──────────
    //
    // YamlDocumentParser.ParseEnvMap retains every 'env' value in its raw
    // scalar form regardless of how it was written — a bare 8080 or true
    // arrives as the literal text "8080"/"true" — so a bare numeric or
    // boolean env value already works at runtime; only the schema (typed
    // strictly "string") rejected it. Likewise 'httpPort' is read via
    // GetScalar + int.TryParse, indifferent to whether the YAML value arrived
    // quoted or bare. These pin the widening promised by
    // SchemaAcceptedCorpusTests.ScalarCoercionCase_WillBeAcceptedInFutureTranche's
    // own remarks once scalar-coercion-env-numeric-value.e2e.yaml and
    // scalar-coercion-httpport-quoted-string.e2e.yaml were promoted out of the
    // scalar-coercion- group.

    [Fact]
    public void Service_EnvBareNumericValue_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  env:
                    RETRY_COUNT: 3
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Service_EnvBareBooleanValue_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  env:
                    FEATURE_FLAG: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// An explicit YAML null must remain rejected even after the type-union
    /// widening above — the widening adds sibling scalar shapes, it does not
    /// open the door to null.
    /// </summary>
    /// <remarks>
    /// Corrected rationale (feat/close-remaining-surfaces, Part B): the
    /// original comment claimed "ParseEnvMap requires a non-null scalar" and
    /// "throws" on '~' — false. With the pinned YamlDotNet 16.3.0
    /// representation model, a YAML '~' scalar is read back as the LITERAL,
    /// ONE-CHARACTER TEXT '~', not as a null value (verified directly against
    /// <c>YamlScalarNode.Value</c>), so <c>ParseEnvMap</c> does NOT throw —
    /// it happily sets the variable to the literal string '~', which is
    /// never what an author means by writing '~'. That is the actual defect
    /// this rejection prevents: a silently-wrong container environment
    /// variable, not a parser exception.
    /// </remarks>
    [Fact]
    public void Service_EnvExplicitNullValue_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  env:
                    FOO: ~
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An explicit null env value must be rejected — the parser would set it to the literal string '~', never what an author means.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app/env/FOO" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Service_HttpPortQuotedString_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: "8080"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Theory]
    [InlineData("\"0\"")]
    [InlineData("\"65536\"")]
    [InlineData("\"999999\"")]
    public void Service_HttpPortQuotedStringOutOfRealPortRange_IsRejected(string quotedPort)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: {{quotedPort}}
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, $"Quoted httpPort {quotedPort} is out of the real TCP port range and must be rejected, exactly like its bare-integer equivalent.");
        var onlyError = Assert.Single(result.Errors,
            e => e.InstanceLocation == "/environment/services/app/httpPort");
        Assert.Contains("[pattern]", onlyError.Message, System.StringComparison.Ordinal);
    }

    // ── Part 2: environment.dependencies ────────────────────────────────────

    [Fact]
    public void Dependency_MissingType_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  version: "16"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An object-shaped dependency missing 'type' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The deliberately-preserved regression: a bare-scalar dependency value
    /// (no top-level "type": "object" on $defs/dependency) must remain
    /// schema-valid, deferred to the parser's own line/column diagnostic —
    /// 'required'/'additionalProperties' do not apply to non-object instances
    /// (JSON Schema draft 2020-12 §6.5.3/§6.3.3), so neither new keyword
    /// touches this shape. Mirrors
    /// Corpus/Accepted/regression-29f910b-dependency-bare-scalar.e2e.yaml.
    /// </summary>
    [Fact]
    public void Dependency_BareScalarValue_RemainsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                db: postgres
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Dependency_UnknownKey_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
                  qeues: [orders]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A misspelled/unknown dependency key must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/qeues" &&
            e.Message.Contains("[additionalProperties] Unknown property 'qeues' on dependency 'db'", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_UnrecognisedType_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: cassandra
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An unrecognised dependency 'type' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/type" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            e.Message.Contains("'cassandra'", System.StringComparison.Ordinal) &&
            // No accepted kind case-insensitively matches 'cassandra' — no
            // suggestion may be fabricated (FormatEnumError's no-guess rule).
            !e.Message.Contains("write '", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// EnvironmentMapper's s_dependencyRegistry looks up 'type' via
    /// StringComparer.OrdinalIgnoreCase — the engine accepts 'Postgres'. This
    /// schema's enum is case-sensitive (matching the canonical lower-case form
    /// used throughout this schema and the DSL docs), so a differently-cased
    /// value is rejected here even though the engine would accept it — the
    /// case-sensitivity finding reported alongside this change.
    /// </summary>
    [Fact]
    public void Dependency_TypeWrongCase_IsRejectedByTheSchemaThoughTheEngineWouldAcceptIt()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: Postgres
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/type" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            // Part C (feat/close-remaining-surfaces): the schema now rejects
            // wrong-case values FIRST on every production path, so the
            // helpful "write the right spelling" message must live here, not
            // only in EnvironmentMapper's own (now-unreachable-until-direct-
            // Map-call) message — see the brief's exact worked example.
            e.Message.Contains("Value 'Postgres' is not one of the accepted values for 'type'", System.StringComparison.Ordinal) &&
            e.Message.Contains("write 'postgres'", System.StringComparison.Ordinal) &&
            // The 13-member enum exceeds MaxListedEnumValues (8): pins that
            // truncation is actually live, not merely implemented.
            e.Message.Contains("… and 5 more", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("mongodb")]
    [InlineData("redis")]
    [InlineData("elasticsearch")]
    [InlineData("rabbitmq")]
    [InlineData("nats")]
    [InlineData("kafka")]
    [InlineData("mailpit")]
    [InlineData("azureservicebus")]
    [InlineData("dynamodb")]
    [InlineData("minio")]
    public void Dependency_EachRegisteredKind_IsAccepted(string kind)
    {
        var yaml = $$"""
            environment:
              dependencies:
                db:
                  type: {{kind}}
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected '{kind}' to be accepted but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// Binds <see cref="Dependency_EachRegisteredKind_IsAccepted"/>'s 13
    /// hardcoded <c>[InlineData]</c> cases to
    /// <see cref="KnownDependencyKinds"/> — the canonical kind list — via
    /// reflection over the Theory's own attributes, rather than duplicating
    /// the 13 literals a second time (which would itself be a THIRD place to
    /// keep in sync, alongside the schema's own enum and
    /// EnvironmentMapper's s_dependencyRegistry). A kind added to
    /// <see cref="KnownDependencyKinds"/> without a matching new
    /// <c>[InlineData]</c> case (or vice versa) fails here with a precise
    /// diff, instead of the Theory silently under/over-covering the real
    /// vocabulary (feat/close-remaining-surfaces, Part D).
    /// </summary>
    [Fact]
    public void DependencyEachRegisteredKindTheory_CoversExactlyKnownDependencyKinds()
    {
        var method = typeof(EnvironmentSchemaTests).GetMethod(
            nameof(Dependency_EachRegisteredKind_IsAccepted),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;

        var theoryKinds = method
            .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
            .Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(method).Single()[0]!)
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToList();

        var canonicalKinds = Vouchfx.Engine.Compilation.Scaffold.KnownDependencyKinds.All
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToList();

        Assert.Equal(canonicalKinds, theoryKinds);
    }

    [Fact]
    public void Dependency_SchemaRegistryOnNonKafkaKind_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
                  schemaRegistry: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'schemaRegistry' on a postgres dependency must be rejected — kafka-only.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/schemaRegistry" &&
            e.Message.Contains(
                "[properties] Property 'schemaRegistry' is not valid on a 'postgres' dependency",
                System.StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_QueuesOnKafka_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: kafka
                  queues: [orders]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'queues' on a kafka dependency must be rejected — azureservicebus-only.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/queues" &&
            e.Message.Contains(
                "[properties] Property 'queues' is not valid on a 'kafka' dependency",
                System.StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_SchemaRegistryOnAzureServiceBus_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: azureservicebus
                  schemaRegistry: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'schemaRegistry' on azureservicebus must be rejected — kafka-only, even for the other broker-shaped kind.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/schemaRegistry" &&
            e.Message.Contains(
                "[properties] Property 'schemaRegistry' is not valid on a 'azureservicebus' dependency",
                System.StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_KafkaWithSchemaRegistry_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  schemaRegistry: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Dependency_AzureServiceBusWithQueuesAndTopics_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                outbox:
                  type: azureservicebus
                  queues: [orders]
                  topics:
                    - name: orders-topic
                      subscriptions: [orders-sub]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// The trap the brief calls out by name: a per-kind allOf/if/then chain of
    /// thirteen clauses must not surface twelve spurious "if"-mismatch entries
    /// alongside the one genuine defect. Mirrors how
    /// SchemaErrorCollectorTests/SchemaErrorCollectionAtScaleTests pin the same
    /// invariant for the (unrelated, much larger) step-type discriminator
    /// chain — this proves IsIfDiscriminatorNoise's suppression generalises,
    /// depth/location-independently, to a SECOND if/then chain living under
    /// $defs/dependency rather than $defs/step.
    /// </summary>
    [Fact]
    public void Dependency_OneBadDependency_YieldsOneErrorNotThirteen()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
                  schemaRegistry: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);

        // Exactly one error, at the offending field's own location, carrying
        // the genuine false-schema failure — none of the other twelve
        // if/then clauses' non-matching 'if' sub-evaluations (each of which
        // would carry its own '[const] Expected "<other-kind>"' message) may
        // leak through as separate "noise" errors.
        Assert.True(result.Errors.Count == 1,
            $"Expected exactly one error, but got {result.Errors.Count}:{System.Environment.NewLine}" +
            string.Join(System.Environment.NewLine, result.Errors.Select(e => $"  at {e.InstanceLocation}: {e.Message}")));
        Assert.Equal("/environment/dependencies/db/schemaRegistry", result.Errors[0].InstanceLocation);
        Assert.Contains(
            "[properties] Property 'schemaRegistry' is not valid on a 'postgres' dependency",
            result.Errors[0].Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void Topics_ItemMissingName_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                asb:
                  type: azureservicebus
                  topics:
                    - subscriptions: [orders-sub]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A topics[] item missing 'name' must be rejected — ParseAsbTopics silently drops it otherwise.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/asb/topics/0" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A topics[] item is nested two levels below its owning dependency (an
    /// array element, not a direct <c>/environment/dependencies/&lt;name&gt;</c>
    /// child), so it falls outside the pointer shape
    /// <c>FormatAdditionalPropertiesError</c> resolves a container name for
    /// (see <c>SchemaErrorCollector</c>'s own remarks on its no-fabrication
    /// rule) and degrades to naming the property alone — still a large
    /// improvement over the old blank-keyword message, and honest rather
    /// than guessing which dependency a topic entry belongs to.
    /// </summary>
    [Fact]
    public void Topics_ItemUnknownKey_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                asb:
                  type: azureservicebus
                  topics:
                    - name: orders-topic
                      description: not a real field
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A topics[] item with an unknown key must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/asb/topics/0/description" &&
            e.Message.Contains("[additionalProperties] Unknown property 'description'", System.StringComparison.Ordinal));
    }

    // ── Part 4a: topics[].name explicit null (alignment fix) ────────────────

    /// <summary>
    /// An explicit null 'name' ('name: ~') previously validated (the type
    /// union included 'null'). Narrowing the type union to exclude 'null'
    /// closes this at schema time instead of surfacing later as an
    /// unrelated-looking Service Bus environment error.
    /// </summary>
    /// <remarks>
    /// Corrected rationale (feat/close-remaining-surfaces, Part B): the
    /// original comment claimed ParseAsbTopics "silently drops" an
    /// explicit-null-named topic entry "exactly as it does an absent 'name'"
    /// — false, and a DIFFERENT defect from the genuinely-true absent-name
    /// case <see cref="Topics_ItemMissingName_IsRejected"/> closes. With the
    /// pinned YamlDotNet 16.3.0 representation model, a YAML '~' scalar is
    /// read back as the LITERAL, ONE-CHARACTER TEXT '~', not as a null value
    /// (verified directly against <c>YamlScalarNode.Value</c>) — so
    /// <c>ParseAsbTopics</c>' pattern <c>YamlScalarNode { Value: { } name }</c>
    /// MATCHES on '~' and the topic is KEPT, with the literal name '~' —
    /// never what an author means by writing '~'. Rejecting it here prevents
    /// the emulator provisioning a topic no author intended, not a silent
    /// drop.
    /// </remarks>
    [Fact]
    public void Topics_ItemNameExplicitNull_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                asb:
                  type: azureservicebus
                  topics:
                    - name: ~
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A topics[] item with an explicit null 'name' must be rejected — the parser would keep the topic, literally named '~', never what an author means.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/asb/topics/0/name" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    // ── Part 5: environment.services/dependencies 'security' block container
    // enrichment (REQ-002; gatekeeper NOTE-1) ───────────────────────────────

    /// <summary>
    /// A 'security' block present but missing its required 'endpoint' must name the
    /// owning dependency, exactly like a depth-4 additionalProperties/properties
    /// rejection already does above (e.g. <see cref="Dependency_UnknownKey_IsRejected"/>)
    /// — not the bare, unqualified "[required] Required properties [...] are not
    /// present" every OTHER 'required' violation in this schema still reports (a step's
    /// own missing fields, a dependency's missing 'type', ...).
    /// </summary>
    [Fact]
    public void Dependency_Security_MissingEndpoint_NamesTheDependency()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    mode: tls
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A security block missing the required 'endpoint' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/cache/security" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal) &&
            e.Message.Contains("on dependency 'cache'", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// An unrecognised key directly inside a 'security' block must name the owning
    /// dependency, exactly like every other closed-object unknown-key rejection this
    /// class pins. Previously this location (one level below the block itself) was too
    /// deep for TryResolveEnvironmentContainer's exact-depth-4 match, so it degraded to
    /// the bare "Unknown property 'bogus'" with no owner named.
    /// </summary>
    [Fact]
    public void Dependency_Security_UnknownKey_NamesTheDependency()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    mode: tls
                    endpoint: 6380
                    bogus: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An unrecognised key inside a security block must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/cache/security/bogus" &&
            e.Message.Contains(
                "[additionalProperties] Unknown property 'bogus' on dependency 'cache'",
                System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The SERVICE-side variant of
    /// <see cref="Dependency_Security_MissingEndpoint_NamesTheDependency"/> — proves the
    /// container enrichment is not dependency-only.
    /// </summary>
    [Fact]
    public void Service_Security_MissingEndpoint_NamesTheService()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    mode: tls
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A service security block missing the required 'endpoint' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/app/security" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal) &&
            e.Message.Contains("on service 'app'", System.StringComparison.Ordinal));
    }

    // ── Part 5b: honest nested attribution below the security object (critic MINOR-6) ──
    //
    // The three tests above pin the boundary's "on" side: a 'required'/'additionalProperties'
    // violation AT the security object itself (missing 'endpoint') or directly ON it (an
    // unrecognised key immediately inside 'security') is accurately described as "on
    // dependency/service '<name>'" — the security block IS a property of that container.
    // A violation BELOW the security object — inside one of its own serverArtifacts[]
    // entries — is a DIFFERENT container (one of potentially several entries), so naming
    // only the outer dependency/service would be honest but imprecise: these two tests pin
    // the boundary's "in ... (at <subpath>)" side, which also locates WHICH entry.

    /// <summary>
    /// A serverArtifacts[] entry missing its required 'source' names the owning
    /// dependency with "in" (not "on") and locates the specific entry via the dotted
    /// sub-path — the worked example from the critic's own MINOR-6 finding.
    /// </summary>
    [Fact]
    public void Dependency_Security_ServerArtifactMissingSource_NamesTheDependencyWithSubPath()
    {
        const string yaml = """
            environment:
              dependencies:
                events-kafka:
                  type: kafka
                  security:
                    mode: tls
                    endpoint: 9093
                    serverArtifacts:
                      - target: /etc/kafka/secrets/kafka.server.keystore.jks
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A serverArtifacts[] entry missing the required 'source' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/events-kafka/security/serverArtifacts/0" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal) &&
            e.Message.Contains(
                "Required properties [\"source\"] are not present in dependency 'events-kafka' " +
                "(at security.serverArtifacts[0])",
                System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>additionalProperties</c> counterpart: an unrecognised key INSIDE a
    /// serverArtifacts[] entry also gets the "in ... (at ...)" form, with the sub-path
    /// extending all the way to the offending property itself (segment 3 onward of the
    /// InstanceLocation, applied uniformly regardless of keyword — see
    /// <c>SchemaErrorCollector.BuildSecuritySubPath</c>).
    /// </summary>
    [Fact]
    public void Dependency_Security_ServerArtifactUnknownKey_NamesTheDependencyWithSubPath()
    {
        const string yaml = """
            environment:
              dependencies:
                events-kafka:
                  type: kafka
                  security:
                    mode: tls
                    endpoint: 9093
                    serverArtifacts:
                      - source: ./certs/kafka.server.keystore.jks
                        target: /etc/kafka/secrets/kafka.server.keystore.jks
                        bogus: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An unrecognised key inside a serverArtifacts[] entry must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/events-kafka/security/serverArtifacts/0/bogus" &&
            e.Message.Contains(
                "[additionalProperties] Unknown property 'bogus' in dependency 'events-kafka' " +
                "(at security.serverArtifacts[0].bogus)",
                System.StringComparison.Ordinal));
    }
}
