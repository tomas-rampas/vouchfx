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
using System.Text.Json.Nodes;
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

    // ── Dependency 'env' (dependency-env REQ-002 / EDGE-001, schema tier) ───
    //
    // $defs/dependency gains an 'env' map whose value-shape rules mirror
    // $defs/service's exactly: a quoted string, a bare numeric and a bare
    // boolean are accepted, an explicit null is rejected. The null rejection is
    // a SCHEMA rule, not a parser rule — YamlDocumentParser.ParseEnvMap retains
    // '~' as the literal one-character text for a dependency exactly as it does
    // for a service, so absent this 'type' restriction no layer would refuse it
    // and the variable would silently reach the container as the string '~'.

    [Fact]
    public void Dependency_EnvStringValue_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
                  env:
                    FOO: "bar"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Dependency_EnvBareNumericValue_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
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
    public void Dependency_EnvBareBooleanValue_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
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
    /// The value-type union is ["string","integer","number","boolean"], but the
    /// three accepted shapes above exercise only string, integer and boolean —
    /// deleting "number" from that union reddens none of them. A bare float
    /// pins it: SchemaResources' scalar type resolver hands 1.5 to the
    /// validator as a JSON number, which 'integer' refuses.
    /// </summary>
    [Fact]
    public void Dependency_EnvBareFloatValue_IsAccepted()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
                  env:
                    SAMPLE_RATE: 1.5
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// An explicit YAML null must be rejected on a dependency for the same
    /// reason it is on a service — and, critically, for the RIGHT reason.
    /// </summary>
    /// <remarks>
    /// Before $defs/dependency carried 'env' at all, this document was already
    /// refused — but as "[additionalProperties] Unknown property 'env' on
    /// dependency 'db'", the right verdict for entirely the wrong reason.
    /// Asserting BOTH the instance location (the offending VALUE, not the 'env'
    /// key) and the '[type]' keyword is what distinguishes "the schema has no
    /// 'env' on a dependency" from "the schema has 'env' and refuses null in
    /// it" — the whole content of this change.
    /// </remarks>
    [Fact]
    public void Dependency_EnvExplicitNullValue_IsRejected()
    {
        const string yaml = """
            environment:
              dependencies:
                db:
                  type: postgres
                  env:
                    FOO: ~
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An explicit null env value must be rejected — the parser would set it to the literal string '~', never what an author means.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/db/env/FOO" &&
            e.Message.Contains("[type]", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// EDGE-001 (dependency-env): a dependency <c>env</c> map constrains an instance
    /// exactly as a service <c>env</c> map does — the same keywords, including the
    /// accepted value shapes. This is the only assertion anywhere that compares the
    /// two — everything else that states the parity states it in prose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The duplication being guarded is DELIBERATE and correct:
    /// <c>$defs/dependency.properties.env</c>'s value rules are an inline COPY of
    /// <c>$defs/service.properties.env</c>'s rather than a shared <c>$defs</c> both
    /// sides <c>$ref</c>, because leaving the service subtree byte-identical is what
    /// keeps the dependency addition purely ADDITIVE, and therefore legal under the v1
    /// schema freeze. That <c>$comment</c> says exactly this; nothing enforced it.
    /// </para>
    /// <para>
    /// Why the copy can drift silently: the dependency <c>env</c> tests above pin the
    /// dependency side's own contract in isolation, and <c>SchemaFreezeTests</c> pins
    /// the whole composed document byte-for-byte — but a DELIBERATE widening of the
    /// SERVICE union regenerates that golden and says nothing at all about the twin
    /// left behind. This repo has already run one sweep of precisely that shape: the
    /// T2a scalar-coercion tranche moved every Core provider's string-valued map to
    /// <c>["string","integer","number","boolean"]</c> in a single pass (see
    /// <c>SchemaAcceptedCorpusTests</c>' retired-tranche remarks). The next such sweep
    /// is the event that would quietly break EDGE-001; this test turns it into a red
    /// build instead.
    /// </para>
    /// <para>
    /// Rooted at the whole <c>env</c> subschema, NOT at its <c>additionalProperties</c>.
    /// The earlier form descended to the value shape before comparing keyword sets, so a
    /// keyword added as a SIBLING of <c>additionalProperties</c> on one side — the
    /// name-level keywords <c>propertyNames</c>, <c>minProperties</c>, <c>maxProperties</c>
    /// most plausibly, since reserved-name work is a name-level rule — propagated nowhere
    /// and left this test green. The <c>description</c>/<c>$comment</c> divergence that
    /// motivated the narrower scope is handled by EXCLUDING those two keys instead: both
    /// are pure annotations with no assertion effect in draft 2020-12, and the two subtrees
    /// state deliberately different prose (the dependency description names the
    /// <c>${conn:...}</c>/<c>${secret:...}</c> refusals that do not apply to a service).
    /// Everything that constrains an instance is compared.
    /// </para>
    /// <para>
    /// Compared as DATA, never as text: keyword sets as ordinal-sorted sequences, a
    /// nested subschema recursively on the same terms, an array-valued keyword (the
    /// type union) as a SET, every other keyword via <see cref="JsonNode.DeepEquals"/>.
    /// A reordered union or reflowed whitespace is not drift and must not redden this.
    /// The set treatment applies to EVERY array-valued keyword, not only the type
    /// union; no order-significant array keyword (<c>prefixItems</c>) appears in either
    /// subtree, and one added later would have its order tolerated here.
    /// </para>
    /// </remarks>
    [Fact]
    public void DependencyEnvSchema_MatchesServiceEnvSchemaExactly()
    {
        // The same root-language schema text YamlSchemaValidator validates every other
        // document in this class against — SchemaResources is its own loader.
        var root = JsonNode.Parse(SchemaResources.ReadRootLanguageSchemaJson())!.AsObject();

        var service = EnvSchema(root, "service");
        var dependency = EnvSchema(root, "dependency");

        // Guards a vacuous pass: two subschemas carrying nothing but the excluded
        // annotations would agree trivially. Asserted at the ROOT only — a nested
        // subschema is legitimately allowed to be empty.
        Assert.NotEmpty(AssertingKeywords(service));

        AssertSubschemasMatch("$defs/{service,dependency}/properties/env", service, dependency);

        static JsonObject EnvSchema(JsonObject schemaRoot, string definition)
        {
            var node = schemaRoot["$defs"]?[definition]?["properties"]?["env"];
            Assert.True(node is JsonObject,
                $"$defs/{definition}/properties/env is missing or is not an object — " +
                "this parity test would otherwise pass vacuously.");
            return (JsonObject)node!;
        }

        // Every keyword that constrains an instance: 'description' and '$comment' are
        // pure annotations (draft 2020-12 §7.7/§8.3 — no assertion effect), and the two
        // subtrees state deliberately different prose in both.
        static string[] AssertingKeywords(JsonObject schema) =>
            schema
                .Select(p => p.Key)
                .Where(k => k != "description" && k != "$comment")
                .OrderBy(k => k, System.StringComparer.Ordinal)
                .ToArray();

        static void AssertSubschemasMatch(string path, JsonObject service, JsonObject dependency)
        {
            var serviceKeywords = AssertingKeywords(service);
            Assert.Equal(serviceKeywords, AssertingKeywords(dependency));

            foreach (var keyword in serviceKeywords)
            {
                var serviceValue = service[keyword];
                var dependencyValue = dependency[keyword];
                var keywordPath = $"{path}/{keyword}";

                if (serviceValue is JsonObject serviceNested && dependencyValue is JsonObject dependencyNested)
                {
                    AssertSubschemasMatch(keywordPath, serviceNested, dependencyNested);
                    continue;
                }

                if (serviceValue is JsonArray serviceUnion && dependencyValue is JsonArray dependencyUnion)
                {
                    Assert.Equal(AsSet(serviceUnion), AsSet(dependencyUnion));
                    continue;
                }

                Assert.True(
                    JsonNode.DeepEquals(serviceValue, dependencyValue),
                    $"'{keywordPath}' has drifted between the service and dependency 'env' schemas: " +
                    $"service={serviceValue?.ToJsonString() ?? "<absent>"}, " +
                    $"dependency={dependencyValue?.ToJsonString() ?? "<absent>"}.");
            }
        }

        // Order-insensitive, whitespace-insensitive rendering of a type union.
        static string AsSet(JsonArray union) =>
            string.Join(
                ",",
                union.Select(v => v?.ToJsonString() ?? "null").OrderBy(v => v, System.StringComparer.Ordinal));
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
            e.Message.Contains("... and 5 more", System.StringComparison.Ordinal));
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
    /// <see cref="Vouchfx.Engine.Compilation.Scaffold.KnownDependencyKinds"/> — the canonical kind list — via
    /// reflection over the Theory's own attributes, rather than duplicating
    /// the 13 literals a second time (which would itself be a THIRD place to
    /// keep in sync, alongside the schema's own enum and
    /// EnvironmentMapper's s_dependencyRegistry). A kind added to
    /// <see cref="Vouchfx.Engine.Compilation.Scaffold.KnownDependencyKinds"/> without a matching new
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
    /// <remarks>
    /// Declared on a KAFKA dependency (M1, second peer-review round): a 'security' block on
    /// any other dependency kind is now rejected outright, and
    /// <c>SchemaErrorCollector.SuppressErrorsInsideForbiddenContainer</c> correctly subsumes
    /// this 'required' finding into that one — <c>required</c> reports against the CONTAINER
    /// missing the property, so both errors land at the identical pointer and the narrowing's
    /// is the one an author must act on. Kafka accepts the block, so this test keeps pinning
    /// REQ-002's endpoint rule and the container attribution around it, and nothing else.
    /// </remarks>
    [Fact]
    public void Dependency_Security_MissingEndpoint_NamesTheDependency()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: tls
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A security block missing the required 'endpoint' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/events/security" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal) &&
            e.Message.Contains("on dependency 'events'", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// An unrecognised key directly inside a 'security' block must name the owning
    /// dependency, exactly like every other closed-object unknown-key rejection this
    /// class pins. Previously this location (one level below the block itself) was too
    /// deep for TryResolveEnvironmentContainer's exact-depth-4 match, so it degraded to
    /// the bare "Unknown property 'bogus'" with no owner named.
    /// </summary>
    /// <remarks>
    /// REQ-020 (authenticated-infrastructure-mtls, slice C): $defs/security now closes
    /// with 'unevaluatedProperties: false' (replacing 'additionalProperties: false'), so
    /// this rejection's own keyword tag changed from '[additionalProperties]' to
    /// '[unevaluatedProperties]' — a genuine, expected consequence of REQ-020's rename,
    /// pinned here rather than left for a future reader to rediscover by surprise. The
    /// "on dependency 'cache'" attribution itself is UNCHANGED: SchemaErrorCollector's
    /// unevaluatedProperties branch resolves the same environment container its
    /// additionalProperties branch already did (see that method's own remarks).
    /// </remarks>
    [Fact]
    public void Dependency_Security_UnknownKey_NamesTheDependency()
    {
        // A KAFKA dependency (M1, second peer-review round): on any other kind the whole
        // 'security' block is now rejected and SuppressErrorsInsideForbiddenContainer subsumes
        // this nested finding into that one — leaving nothing at the pinned location. Kafka
        // accepts the block, so this keeps pinning REQ-020's closure and nothing else.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: 9093
                    bogus: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An unrecognised key inside a security block must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/events/security/bogus" &&
            e.Message.Contains(
                "[unevaluatedProperties] Unknown property 'bogus' on dependency 'events'",
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
                    profile: tls
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
                    profile: tls
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
                    profile: tls
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

    // ── Part 5c: REQ-021 — 'security' legality is narrowed per target kind ────────────
    //
    // $defs/dependency's own final allOf clause forbids the whole 'security' block for
    // every dependency kind EXCEPT kafka (the only dependency kind this release wires a
    // client connection for), via an allow-list ('not: { const: "kafka" }' in the 'if'
    // condition) rather than an enumerated exclusion of every other kind. Freeze-critical
    // because it is a NARROWING (REQ-021's own rationale): added after 1.0 it would reject
    // suites that validated before.
    //
    // M1 (second peer-review round) TIGHTENED this from a 'profile'-pinned-to-'tls' clause
    // to an outright block rejection. The old shape legalised 'profile: tls' on kinds
    // nothing in this release stages a TLS client connection for, so the engine-side
    // REQ-005 probe would confirm the endpoint speaks TLS while the step's own client
    // connected in plaintext — the false assurance REQ-022 exists to close. Since the
    // narrowing gates which suites VALIDATE, rejecting more now and widening in 1.1 (as
    // REQ-013 lands server-side TLS for the remaining kinds) is the only safe direction.

    /// <summary>
    /// RED-FIRST EVIDENCE for REQ-021: 'profile: mtls' on a non-kafka dependency kind
    /// (redis) is rejected, naming BOTH the kind and the owning dependency in one message.
    /// Kept as a permanent regression test, not merely a throwaway red-phase check: this
    /// schema fixture pins the FIRST-CLASS enforcement of the narrowing, independent of the
    /// corpus gate's own coverage.
    /// </summary>
    /// <remarks>
    /// The pinned location moved one segment shallower in M1 — from
    /// <c>.../security/profile</c> (the old <c>const</c> pin) to <c>.../security</c> (the
    /// block the clause now rejects) — and the message deliberately no longer names the
    /// offending PROFILE, because the profile is not what is wrong: no profile is wired for
    /// this kind, so naming <c>mtls</c> would invite an author to try <c>tls</c> instead,
    /// which does not work either. The sibling test below pins exactly that.
    /// </remarks>
    [Fact]
    public void Dependency_SecurityMtls_OnNonKafkaKind_IsRejected_NamingKindAndDependency()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: mtls
                    endpoint: 6380
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "'profile: mtls' on a redis dependency must be rejected — no security profile is wired " +
            "for redis (REQ-021).");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/cache/security" &&
            e.Message.Contains("redis", System.StringComparison.Ordinal) &&
            e.Message.Contains("cache", System.StringComparison.Ordinal) &&
            e.Message.Contains("no security profile is wired", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The positive control: 'profile: mtls' on a kafka dependency — the one kind this
    /// release DOES wire client certificates for — still validates. Kafka needs no clause
    /// of its own in the allow-list narrowing; its 'type' simply fails the 'not: { const:
    /// "kafka" }' condition, so the 'tls'-only pin never applies to it.
    /// </summary>
    [Fact]
    public void Dependency_SecurityMtls_OnKafka_IsValid()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// The service-side positive control: 'profile: mtls' on a declared SERVICE — never
    /// narrowed by REQ-021's per-kind clause at all, since $defs/service carries no such
    /// allow-list (only $defs/dependency does) — still validates.
    /// </summary>
    [Fact]
    public void Service_SecurityMtls_IsValid()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// 'profile: tls' is rejected on EVERY non-kafka dependency kind, exactly like 'mtls' —
    /// the narrowing is a whole-block restriction, never a profile-specific one. Exercised
    /// across a representative spread of non-kafka kinds (not merely redis, the kind the
    /// negative-control test above already covers), each asserting the SAME message an
    /// author sees, so a partial regression that re-legalised one kind is caught.
    /// </summary>
    /// <remarks>
    /// m2 + M1 (second peer-review round). This theory previously asserted the OPPOSITE —
    /// that 'tls' stayed valid on every kind — and its docstring drew a conclusion the
    /// fixtures never supported: "proven to be 'mtls'-specific, not an accidental blanket
    /// restriction". It proved only that 'tls' was legal everywhere; nothing here ever
    /// exercised a THIRD profile, which the old clause rejected on these kinds just as
    /// firmly as 'mtls' (the schema's own $comment and the CHANGELOG both said so at the
    /// time — this test contradicted them). M1 then removed the distinction entirely: no
    /// profile is legal on a non-kafka dependency at 1.0, so the inverted assertion below is
    /// now the accurate one.
    /// </remarks>
    [Theory]
    [InlineData("postgres")]
    [InlineData("mongodb")]
    [InlineData("rabbitmq")]
    // NIT-1 (peer review, fix round 3): 'elasticsearch' and 'azureservicebus' are the two
    // vowel-initial kinds, and are here so the message's own article agreement is exercised
    // rather than reasoned about. The message names the kind without an article at all —
    // "dependency kind 'elasticsearch'" — because the article cannot agree with a value that
    // is data; these two cases are what proves the wording never regresses to "a '<kind>'".
    [InlineData("elasticsearch")]
    [InlineData("azureservicebus")]
    public void Dependency_SecurityTls_OnNonKafkaKinds_IsRejected(string dependencyType)
    {
        var yaml = $$"""
            environment:
              dependencies:
                dep:
                  type: {{dependencyType}}
                  security:
                    profile: tls
                    endpoint: 9999
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            $"Expected 'profile: tls' on a '{dependencyType}' dependency to be REJECTED — no security " +
            "profile is wired for that kind in this release (REQ-021, as tightened by M1).");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/dependencies/dep/security" &&
            e.Message.Contains($"no security profile is wired for dependency kind '{dependencyType}'",
                System.StringComparison.Ordinal) &&
            !e.Message.Contains($"a '{dependencyType}' dependency", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The positive control for the theory above: 'profile: tls' on a KAFKA dependency —
    /// the one dependency kind this release wires — still validates, so the narrowing above
    /// is proven to be per-KIND rather than a blanket rejection of the field.
    /// </summary>
    [Fact]
    public void Dependency_SecurityTls_OnKafka_IsValid()
    {
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: 9093
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// The narrowing is a WHOLE-BLOCK rejection, not a rejection of the 'profile' field:
    /// every other field an author might have written inside the block is rejected with it,
    /// and — the point this pins — with EXACTLY ONE error, not one per field. See
    /// <c>SchemaErrorCollector.SuppressErrorsInsideForbiddenContainer</c> (H-A).
    /// </summary>
    [Fact]
    public void Dependency_Security_OnNonKafkaKind_YieldsExactlyOneError_WhateverIsInsideTheBlock()
    {
        // Three independent defects INSIDE the block — a wrong-cased profile (a [pattern]
        // miss), a blank caCert (a [minLength] miss) and an unrecognised key (an
        // [unevaluatedProperties] miss) — plus the block-level rejection itself. Before the
        // subsumption pass this shape produced four errors, three of them about the contents
        // of a block that may not be declared at all.
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: TLS
                    endpoint: 6380
                    caCert: ""
                    bogus: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/environment/dependencies/cache/security", error.InstanceLocation);
        Assert.Contains("no security profile is wired for dependency kind 'redis'",
            error.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// MAJOR-1 (peer review, fix round 3) — the SURVIVES half of the pair above.
    /// <c>SchemaErrorCollector.SuppressErrorsInsideForbiddenContainer</c> is the widest
    /// suppression rule in that class: it drops EVERY error at or below any location a
    /// boolean-<c>false</c> subschema rejected. Every other test for it asserts
    /// <c>Assert.Single</c> on a document whose ONLY defect region is the forbidden container,
    /// so not one of them can fail if the containment check over-reaches — and over-suppression
    /// is the direction that class's own remarks call the dangerous one, which is why the
    /// <c>NestedAnyOf_*</c> pair exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion that actually pins the containment check is the <c>securityExtra</c> one.
    /// A raw <see cref="string.StartsWith(string, System.StringComparison)"/> on the forbidden
    /// container's pointer accepts <c>/environment/dependencies/cache/securityExtra</c> as
    /// "inside" <c>/environment/dependencies/cache/security</c> — the two share a character
    /// prefix but not a JSON Pointer SEGMENT boundary — and silently mutes a genuine unknown-key
    /// error on a property that merely starts with the same letters. Measured: dropping the
    /// segment-boundary check makes exactly this assertion fail and no other test in the
    /// repository fail, which is why the case is written down here rather than left to the
    /// reader of <c>IsPathOrDescendant</c>'s own remarks.
    /// </para>
    /// <para>
    /// The two SIBLING containers (<c>cache</c>, <c>cache-2</c>) and the independent defect on
    /// <c>events</c> are pinned alongside it because they are the shapes a reader expects a
    /// containment rule to get wrong, not because a prefix-naive check breaks them: a
    /// forbidden-shape error is never subsumed (the rule short-circuits on it before the
    /// containment test runs), so both containers report under either spelling, and
    /// <c>/environment/dependencies/events/bogus</c> shares no character prefix with either
    /// container. Measured, not assumed — see this test's own red run.
    /// </para>
    /// </remarks>
    [Fact]
    public void Dependency_Security_ForbiddenContainer_DoesNotMuteSiblingsOrPrefixSharingProperties()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  securityExtra: true
                  security:
                    profile: tls
                    endpoint: 6380
                cache-2:
                  type: redis
                  security:
                    profile: tls
                    endpoint: 6381
                events:
                  type: kafka
                  bogus: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);

        var dump = string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));
        var locations = result.Errors
            .Select(e => e.InstanceLocation)
            .OrderBy(l => l, System.StringComparer.Ordinal)
            .ToList();

        Assert.True(locations.Contains("/environment/dependencies/cache/security"),
            $"The first forbidden 'security' container must report. Errors: {dump}");
        Assert.True(locations.Contains("/environment/dependencies/cache-2/security"),
            "A SIBLING dependency's own forbidden 'security' container must report independently — " +
            $"one container's rejection may never stand in for another's. Errors: {dump}");
        Assert.True(locations.Contains("/environment/dependencies/cache/securityExtra"),
            "An unknown key on the SAME dependency whose NAME merely begins with 'security' is not " +
            "inside the forbidden container — it shares a character prefix, not a JSON Pointer " +
            $"segment boundary — and must survive the subsumption pass. Errors: {dump}");
        Assert.True(locations.Contains("/environment/dependencies/events/bogus"),
            "An independent defect outside every forbidden container must survive alongside them — " +
            $"the suppression is scoped by containment, never document-wide. Errors: {dump}");

        Assert.True(locations.Count == 4, $"Expected exactly those four errors. Errors: {dump}");
    }

    /// <summary>
    /// SEC-4 (security review, fix round 3): both author-controlled values this message
    /// interpolates — the dependency's NAME and its declared <c>type</c> — are bounded at
    /// <c>SchemaErrorCollector</c>'s own 200-character display limit. M1 made this the message an
    /// author hits first for a misplaced <c>security</c> block, and neither value has a length
    /// limit of its own: the name is a YAML key, and the kind is whatever <c>type</c> holds,
    /// which on this exact path has already failed its own closed enum and so may be arbitrary.
    /// </summary>
    /// <remarks>
    /// Both are genuinely reachable through the real validator, which is why this is a document
    /// test rather than a unit call: an over-long <c>type</c> still satisfies the narrowing
    /// clause's <c>not: { const: "kafka" }</c> condition, so the <c>then</c> branch fires and the
    /// block is rejected with the long value in hand. The enum violation on <c>type</c> is a
    /// SIBLING of the forbidden container, not a descendant, so it survives alongside — asserted
    /// here too, because it is the same containment property MAJOR-1's survivorship test pins.
    /// </remarks>
    [Fact]
    public void Dependency_Security_ForbiddenMessage_BoundsTheDependencyNameAndKind()
    {
        var longName = new string('a', 250);
        var longKind = new string('b', 250);

        var yaml = $$"""
            environment:
              dependencies:
                {{longName}}:
                  type: {{longKind}}
                  security:
                    profile: tls
                    endpoint: 6380
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);

        var forbidden = Assert.Single(result.Errors,
            e => e.InstanceLocation == $"/environment/dependencies/{longName}/security");

        Assert.DoesNotContain(longName, forbidden.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(longKind, forbidden.Message, System.StringComparison.Ordinal);
        Assert.Contains($"'{new string('a', 200)}... (250 chars total)'", forbidden.Message,
            System.StringComparison.Ordinal);
        Assert.Contains($"dependency kind '{new string('b', 200)}... (250 chars total)'", forbidden.Message,
            System.StringComparison.Ordinal);

        // The message stays O(1) in both values rather than a multiple of them.
        Assert.True(forbidden.Message.Length < 1000,
            $"Message must be O(1) in the offending values; was {forbidden.Message.Length} chars.");

        // The sibling 'type' enum violation is NOT inside the forbidden container and survives.
        Assert.Contains(result.Errors,
            e => e.InstanceLocation == $"/environment/dependencies/{longName}/type");
    }

    // ── Part 3: environment.services.ports / healthCheck (services-generalisation, REQ-008/009) ──

    /// <summary>
    /// REQ-008: a service may declare one or more TCP ports as a bare array of integers.
    /// </summary>
    [Fact]
    public void Service_Ports_ArrayOfIntegers_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093, 9094]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// REQ-008 decision: 'ports' is declared with 'minItems: 1' — an empty array carries no
    /// declared endpoint at all and is rejected rather than silently accepted as a no-op.
    /// </summary>
    [Fact]
    public void Service_Ports_EmptyArray_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: []
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An empty 'ports' array must be rejected (minItems: 1).");
        Assert.Contains(result.Errors, e => e.InstanceLocation == "/environment/services/kafka-broker/ports");
    }

    /// <summary>
    /// REQ-008: each 'ports' entry is bounded to the real TCP port range, mirroring
    /// 'httpPort''s own range check.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Service_Ports_PortOutOfRange_IsRejected(int port)
    {
        var yaml = $$"""
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [{{port}}]
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, $"Port {port} is out of the real TCP port range and must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/kafka-broker/ports/0");
    }

    /// <summary>
    /// REQ-009: 'healthCheck: { type: tcp, port: N }' is accepted.
    /// </summary>
    [Fact]
    public void Service_HealthCheckTcp_WithPort_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    type: tcp
                    port: 9093
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// REQ-009: 'type: tcp' REQUIRES 'port' — omitting it is rejected.
    /// </summary>
    [Fact]
    public void Service_HealthCheckTcp_MissingPort_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    type: tcp
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'healthCheck: { type: tcp }' with no 'port' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/kafka-broker/healthCheck" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal) &&
            e.Message.Contains("in service 'kafka-broker' (at healthCheck)", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// REQ-009: 'type: http' with an explicit 'path' is accepted — the explicit spelling of
    /// today's default behaviour.
    /// </summary>
    [Fact]
    public void Service_HealthCheckHttp_WithPath_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                web:
                  image: traefik/whoami
                  healthCheck:
                    type: http
                    path: /healthz
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// REQ-009 field-requiredness table: 'port' is not applicable to 'type: http' and is
    /// rejected outright, mirroring $defs/security's own profile-conditional forbidden-field
    /// idiom.
    /// </summary>
    [Fact]
    public void Service_HealthCheckHttp_WithPort_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                web:
                  image: traefik/whoami
                  healthCheck:
                    type: http
                    port: 8080
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'healthCheck: { type: http, port: ... }' must be rejected — 'port' is tcp-only.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/web/healthCheck/port");
    }

    /// <summary>
    /// REQ-009 field-requiredness table: 'path' is not applicable to 'type: tcp' and is
    /// rejected outright.
    /// </summary>
    [Fact]
    public void Service_HealthCheckTcp_WithPath_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    type: tcp
                    port: 9093
                    path: /healthz
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'healthCheck: { type: tcp, path: ... }' must be rejected — 'path' is http-only.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/kafka-broker/healthCheck/path");
    }

    /// <summary>
    /// An unrecognised key inside 'healthCheck' is rejected (additionalProperties: false).
    /// </summary>
    [Fact]
    public void Service_HealthCheck_UnknownKey_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    type: tcp
                    port: 9093
                    bogus: true
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An unrecognised key inside 'healthCheck' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/kafka-broker/healthCheck/bogus");
    }

    /// <summary>
    /// 'healthCheck.type' is matched case-sensitively, like every other DSL vocabulary term.
    /// </summary>
    [Fact]
    public void Service_HealthCheckType_WrongCase_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    type: TCP
                    port: 9093
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'healthCheck.type: TCP' must be rejected — only the lower-case spelling is recognised.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/kafka-broker/healthCheck/type");
    }

    /// <summary>
    /// 'healthCheck' requires 'type'.
    /// </summary>
    [Fact]
    public void Service_HealthCheck_MissingType_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    port: 9093
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'healthCheck' with no 'type' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/environment/services/kafka-broker/healthCheck" &&
            e.Message.Contains("[required]", System.StringComparison.Ordinal) &&
            e.Message.Contains("in service 'kafka-broker' (at healthCheck)", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// MINOR-2(a) (peer review, fix round 4): two NESTED forbidden containers BOTH survive
    /// <c>SchemaErrorCollector.SuppressErrorsInsideForbiddenContainer</c> — the outer one does
    /// not subsume the inner one, because every forbidden-shape error short-circuits into the
    /// survivor list before the containment test runs.
    /// </summary>
    /// <remarks>
    /// That method's remarks used to assert the OPPOSITE ("two NESTED forbidden containers
    /// collapse to the outermost"), and the finding that caught it supposed the case was probably
    /// unreachable because <c>security</c> is the only object-valued forbidden property. Both are
    /// wrong, and this is the measurement: a <c>project</c>-form service forbids
    /// <c>healthCheck</c> outright (<c>$defs/service</c>'s own project-form clause), while
    /// <c>$defs/serviceHealthCheck</c>'s <c>type: http</c> clause forbids <c>port</c> INSIDE that
    /// same, already-forbidden object. Both errors report. The test exists so the corrected
    /// sentence stays true: a future change that DOES collapse the nested case (by exempting a
    /// strictly-nested forbidden error from the short-circuit) must update this test and the
    /// remarks together, rather than silently re-diverging them.
    /// </remarks>
    [Fact]
    public void Service_NestedForbiddenContainers_BothSurvive()
    {
        const string yaml = """
            environment:
              services:
                app:
                  project: ./src/App/App.csproj
                  healthCheck:
                    type: http
                    port: 8080
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);

        var dump = string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));
        var locations = result.Errors.Select(e => e.InstanceLocation).ToList();

        Assert.True(locations.Contains("/environment/services/app/healthCheck"),
            $"The OUTER forbidden container must report. Errors: {dump}");
        Assert.True(locations.Contains("/environment/services/app/healthCheck/port"),
            "The INNER forbidden property must report alongside it — a forbidden-shape error is " +
            "never subsumed, not even by an enclosing forbidden container. Errors: " + dump);
        Assert.True(locations.Count == 2, $"Expected exactly those two errors. Errors: {dump}");
    }

    /// <summary>
    /// MAJOR-1 (peer review, fix round 4): the <c>dependencyKind is null</c> branch of
    /// <c>SchemaErrorCollector.FormatForbiddenPropertyError</c>'s <c>security</c> case is
    /// reachable, and carries the same corrected text as the qualified branch.
    /// </summary>
    /// <remarks>
    /// A non-scalar <c>type</c> still satisfies the narrowing clause's own
    /// <c>not: { const: "kafka" }</c> condition, so the <c>then</c> branch fires and rejects the
    /// <c>security</c> block — while <c>TryResolveContainerType</c> has no scalar to read a kind
    /// from and returns <see langword="null"/>. Measured before this round: this branch shipped
    /// the bare "declare it under 'environment.services' instead" advice, which is a dead end for
    /// every kind it can fire on. The <c>type</c> errors survive alongside because they are a
    /// SIBLING of the forbidden container, not a descendant.
    /// </remarks>
    [Fact]
    public void Dependency_Security_NonScalarType_UsesTheUnqualifiedMessage()
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: [redis, postgres]
                  security:
                    profile: tls
                    endpoint: 6380
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);

        var dump = string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));
        var forbidden = Assert.Single(result.Errors,
            e => e.InstanceLocation == "/environment/dependencies/cache/security");

        Assert.Contains("no security profile is wired for this dependency kind in this release",
            forbidden.Message, System.StringComparison.Ordinal);
        Assert.Contains("only a 'kafka' dependency, or a declared service, can carry a 'security' block today",
            forbidden.Message, System.StringComparison.Ordinal);
        Assert.Contains("arrives in 1.1", forbidden.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("environment.services' instead", forbidden.Message,
            System.StringComparison.Ordinal);
        Assert.True(result.Errors.Any(e => e.InstanceLocation == "/environment/dependencies/cache/type"),
            $"The sibling 'type' violation must survive alongside the forbidden container. Errors: {dump}");
    }

    // ── Part 6: environment.services.endpoint (project-form endpoint selection) ──
    //
    // 'endpoint' names WHICH of a project's discovered launch-profile endpoints
    // the engine stages as that service's address. Two schema-tier rules ship
    // with it, both per-field boolean 'false' subschemas in $defs/service's
    // existing 'allOf' (never a 'oneOf'/'not' as an ENFORCEMENT mechanism — see
    // that definition's own description for the measured reason):
    //   • 'endpoint' is forbidden once 'image' is set — an image-form service
    //     already selects what it exposes through httpPort/ports/security.endpoint.
    //   • 'httpPort' joins 'ports'/'healthCheck' in the project-form clause. It
    //     was previously accepted there and silently ignored (EnvironmentMapper
    //     reads it only in the image branch), which is exactly the failure mode
    //     that clause exists to close. A pre-GA narrowing, not an addition.
    //
    // Every case below pins the error COUNT as well as its location, because
    // composite-branch noise is the standing hazard in this schema: 'anyOf' has
    // a satisfied branch in all four documents (each declares exactly one of
    // image/project), so the ONLY error may ever be the one the case is about.

    /// <summary>
    /// A project-form service may declare 'endpoint' — the accept half.
    /// </summary>
    [Fact]
    public void Service_EndpointOnProjectForm_IsAccepted()
    {
        const string yaml = """
            environment:
              services:
                app:
                  project: ./src/App/App.csproj
                  endpoint: https
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        var dump = string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));
        Assert.True(result.IsValid, $"A project-form service declaring 'endpoint' must validate. Errors: {dump}");
    }

    /// <summary>
    /// 'httpPort' is refused on a project-form service, naming the field AND the
    /// field that replaces it — exactly one error, no composite-branch noise.
    /// </summary>
    /// <remarks>
    /// The remedy half is the part worth pinning. This refusal is a BREAKING
    /// CHANGE: the field used to be accepted here and silently ignored, so the
    /// first thing an author of a previously-green suite sees is this string,
    /// in the output of the run that broke. The generic "not valid on service
    /// '&lt;name&gt;'" text every other forbidden service property gets would
    /// leave them deleting a line with no idea what takes its place, which is
    /// why <c>SchemaErrorCollector.FormatForbiddenPropertyError</c> carries a
    /// bespoke branch for this one property. Assert the substance — the
    /// port/listener distinction and the word <c>endpoint</c> — not just the
    /// prefix, so a future reword cannot quietly drop the remedy and stay green.
    /// </remarks>
    [Fact]
    public void Service_HttpPortOnProjectForm_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  project: ./src/App/App.csproj
                  httpPort: 8080
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "'httpPort' has no effect on a project-form service and must be rejected, not silently ignored.");
        // 8080 satisfies httpPort's OWN base schema (integer, in range), so the
        // boolean-'false' rejection is the only thing that can report here.
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/environment/services/app/httpPort", onlyError.InstanceLocation);
        Assert.Contains(
            "[properties] Property 'httpPort' is not valid on the 'project'-form service 'app' - "
            + "a project's endpoints are discovered from its own launch profile, and 'httpPort' "
            + "names a container port rather than a listener. Remove the line; to choose WHICH "
            + "discovered endpoint this service is addressed on, use 'endpoint' (DSL section 3.2)",
            onlyError.Message,
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>httpPort</c> refusal bounds the service NAME it echoes, at the same
    /// 200-character display limit the <c>security</c> branch has always applied — one bound
    /// covering both, not two message shapes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This branch was unbounded when it shipped, and it is the branch that most needed the
    /// bound: the refusal is a breaking change, so its message is the first thing an author of a
    /// previously-green suite reads, and the remedy sentence naming <c>endpoint</c> sits at the
    /// END of it — behind however many characters the service key happens to be. An unbounded
    /// key pushes the only actionable half of the message off the terminal.
    /// </para>
    /// <para>
    /// Legibility, not safety: the value is the author's own YAML key rendered back to the
    /// author. The assertion is therefore about SIZE and about the remedy surviving, never about
    /// the key being neutralised.
    /// </para>
    /// </remarks>
    [Fact]
    public void Service_HttpPortOnProjectForm_BoundsTheServiceName()
    {
        var longName = new string('s', 250);

        var yaml = $$"""
            environment:
              services:
                {{longName}}:
                  project: ./src/App/App.csproj
                  httpPort: 8080
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);

        var onlyError = Assert.Single(result.Errors);
        Assert.Equal($"/environment/services/{longName}/httpPort", onlyError.InstanceLocation);

        Assert.DoesNotContain(longName, onlyError.Message, System.StringComparison.Ordinal);
        Assert.Contains(
            $"service '{new string('s', 200)}... (250 chars total)' - ",
            onlyError.Message,
            System.StringComparison.Ordinal);

        // The remedy is the half a long name would have buried, so pin that it still arrives.
        Assert.EndsWith(
            "use 'endpoint' (DSL section 3.2)", onlyError.Message, System.StringComparison.Ordinal);

        // O(1) in the offending key rather than a multiple of it.
        Assert.True(onlyError.Message.Length < 1000,
            $"Message must be O(1) in the service name; was {onlyError.Message.Length} chars.");
    }

    /// <summary>
    /// 'endpoint' is refused on an image-form service, naming the field —
    /// exactly one error.
    /// </summary>
    [Fact]
    public void Service_EndpointOnImageForm_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  endpoint: https
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'endpoint' is project-form only and must be rejected on an image-form service.");
        // 'https' satisfies endpoint's own base schema (string, minLength 1), so
        // the boolean-'false' rejection is the only thing that can report here.
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/environment/services/app/endpoint", onlyError.InstanceLocation);
        Assert.Contains(
            "[properties] Property 'endpoint' is not valid on service 'app'",
            onlyError.Message,
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// An EMPTY 'endpoint' is refused by 'minLength: 1'. A whitespace-only value
    /// is deliberately NOT refused here — it is a legal string that names no
    /// endpoint, and the topology-build-time match is the layer that can say so
    /// while listing what the project actually declares.
    /// </summary>
    [Fact]
    public void Service_EmptyEndpoint_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  project: ./src/App/App.csproj
                  endpoint: ""
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "An empty 'endpoint' names no endpoint and must be rejected (minLength: 1).");
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/environment/services/app/endpoint", onlyError.InstanceLocation);
        Assert.Contains("[minLength]", onlyError.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A PORT NUMBER is refused: 'endpoint' takes an endpoint NAME, and both
    /// fields an author is likely to migrate from are numeric ('httpPort', and
    /// 'security.endpoint', which explicitly accepts a port). Pinned because it
    /// is the most likely author mistake against this field and the only new
    /// refusal in this part whose message does NOT name 'endpoint' — the
    /// located, singular '[type]' error is the whole diagnosis an author gets,
    /// so a regression that widened the type or split this into composite noise
    /// would degrade the one signal available here without failing anything
    /// else.
    /// </summary>
    [Fact]
    public void Service_NumericEndpoint_IsRejected()
    {
        const string yaml = """
            environment:
              services:
                app:
                  project: ./src/App/App.csproj
                  endpoint: 5001
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "'endpoint' names a launch-profile endpoint, never a port, so a number must be rejected.");
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/environment/services/app/endpoint", onlyError.InstanceLocation);
        Assert.Contains("[type]", onlyError.Message, System.StringComparison.Ordinal);
    }
}
