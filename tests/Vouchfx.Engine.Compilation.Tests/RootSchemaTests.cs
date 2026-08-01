// S02-C-01 — JSON Schema: four top-level sections.
//
// These tests prove the root-language JSON Schema (draft 2020-12) correctly
// validates the four top-level sections of a `.e2e.yaml` file, and that
// <see cref="YamlSchemaValidator"/> surfaces useful, located error messages.
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Vouchfx.Steps.CacheAssert.Redis;
using Vouchfx.Steps.DbAssert.Dynamodb;
using Vouchfx.Steps.DbAssert.Mongodb;
using Vouchfx.Steps.DbAssert.Mysql;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.DbAssert.SqlServer;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MailExpect.Smtp;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqExpect.Nats;
using Vouchfx.Steps.MqExpect.Rabbitmq;
using Vouchfx.Steps.MqExpect.Redis;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.Steps.MqPublish.Nats;
using Vouchfx.Steps.MqPublish.Rabbitmq;
using Vouchfx.Steps.MqPublish.Redis;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.StorageAssert.S3;
using Vouchfx.Steps.TraceExpect.Otlp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S02-C-01: Root-language JSON Schema acceptance tests.
/// </summary>
public sealed class RootSchemaTests
{
    // -------------------------------------------------------------------------
    // Minimal valid document
    // -------------------------------------------------------------------------

    /// <summary>
    /// A document containing only the mandatory <c>steps</c> section with one
    /// step that carries <c>id</c> and <c>type</c> must pass schema validation.
    /// </summary>
    [Fact]
    public void Validate_MinimalValidYaml_IsValid()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid, $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // metadata.schemaVersion: const "v1" — a rejection hook for a future v2
    // -------------------------------------------------------------------------

    /// <summary>
    /// The field remains optional: a document that omits it entirely must
    /// still be valid.
    /// </summary>
    [Fact]
    public void Validate_MetadataSchemaVersionOmitted_IsAccepted()
    {
        const string yaml = """
            metadata:
              name: "no schemaVersion declared"
            steps:
              - id: s1
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Validate_MetadataSchemaVersionV1_IsAccepted()
    {
        const string yaml = """
            metadata:
              schemaVersion: v1
            steps:
              - id: s1
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// Any value other than the literal 'v1' is rejected — the field is now a
    /// real rejection hook for a future v2 language schema, not decoration.
    /// </summary>
    /// <remarks>
    /// m4 (third-round gatekeeper finding): before SchemaErrorCollector grew
    /// an <c>IsConstShape</c>/<c>FormatConstError</c> branch, this surfaced
    /// raw JsonSchema.Net library text with literal escape sequences —
    /// <c>[const] Expected ""v1""</c> — the only new user-facing keyword this
    /// branch's error-message work had left un-enriched. The message now
    /// names what was written and what to write instead, read from the LIVE
    /// schema's own <c>const</c> value (never hardcoded), exactly as
    /// <c>[enum]</c> already does for dependency <c>type</c>/<c>imagePullPolicy</c>/
    /// <c>verifyMode</c>.
    /// </remarks>
    [Fact]
    public void Validate_MetadataSchemaVersionV2_IsRejected()
    {
        const string yaml = """
            metadata:
              schemaVersion: v2
            steps:
              - id: s1
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A schemaVersion other than 'v1' must be rejected by the const constraint.");
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/metadata/schemaVersion", onlyError.InstanceLocation);
        Assert.Contains("[const]", onlyError.Message, System.StringComparison.Ordinal);
        Assert.Contains("'v2'", onlyError.Message, System.StringComparison.Ordinal);
        Assert.Contains("'v1'", onlyError.Message, System.StringComparison.Ordinal);
        Assert.Contains("omit", onlyError.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Expected", onlyError.Message, System.StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Missing required 'steps' section
    // -------------------------------------------------------------------------

    /// <summary>
    /// A document with only <c>metadata</c> and no <c>steps</c> section must be
    /// rejected; at least one error must reference the root location or mention
    /// <c>steps</c>, so the author understands what is missing.
    /// </summary>
    [Fact]
    public void Validate_MissingSteps_IsRejectedWithUsefulMessage()
    {
        const string yaml = """
            metadata:
              name: "incomplete test"
              owner: "payments-team"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        // At least one error must mention "steps" or sit at the root pointer.
        var mentionsStepsOrRoot = result.Errors.Any(e =>
            e.Message.Contains("steps", System.StringComparison.OrdinalIgnoreCase) ||
            e.InstanceLocation == "" ||
            e.InstanceLocation == "/");

        Assert.True(mentionsStepsOrRoot,
            $"Expected an error referencing 'steps' or the root, got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // -------------------------------------------------------------------------
    // Step missing required 'id' field
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step that carries <c>type</c> but omits the required <c>id</c> field must
    /// be rejected; the error location must point at the offending step entry
    /// (i.e. somewhere under <c>/steps/0</c>).
    /// </summary>
    [Fact]
    public void Validate_StepMissingId_IsRejected()
    {
        const string yaml = """
            steps:
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        // At least one error must be located at or under /steps/0.
        var pointsAtStep = result.Errors.Any(e =>
            e.InstanceLocation.StartsWith("/steps/0", System.StringComparison.Ordinal) ||
            e.InstanceLocation.StartsWith("/steps", System.StringComparison.Ordinal));

        Assert.True(pointsAtStep,
            $"Expected an error at /steps/0 or /steps, got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // -------------------------------------------------------------------------
    // Invalid verifyMode enum value
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step that sets <c>verifyMode</c> to a value that DIFFERS FROM AN
    /// ACCEPTED ONE ONLY BY CASE (not <c>IMMEDIATE</c> or <c>RETRY</c>) must
    /// be rejected, located exactly at the offending field, with an
    /// actionable <c>[enum]</c> message that names the correct spelling.
    /// </summary>
    /// <remarks>
    /// Strengthened (feat/close-remaining-surfaces): the original assertion
    /// pinned only <c>IsValid == false</c>, unlike its neighbouring
    /// <c>schemaVersion</c>/enum-shaped tests above, which already pin
    /// location and keyword. Also pins the Part C <c>[enum]</c> enrichment
    /// for a case-insensitive match ('retry' -&gt; 'RETRY'). See
    /// <see cref="Validate_VerifyModeUnrecognisedValue_IsRejectedWithNoFabricatedSuggestion"/>
    /// for the CONTRASTIVE case (a value matching nothing at all, not merely
    /// by case) this summary's original, broader "undeclared value" wording
    /// implied but did not itself exercise (MINOR-11).
    /// </remarks>
    [Fact]
    public void Validate_VerifyModeInvalidEnum_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                verifyMode: retry
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A lower-cased verifyMode value must be rejected by the case-sensitive schema enum.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/verifyMode" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            e.Message.Contains("'retry'", System.StringComparison.Ordinal) &&
            e.Message.Contains("IMMEDIATE", System.StringComparison.Ordinal) &&
            e.Message.Contains("write 'RETRY'", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The contrastive case (MINOR-11): a <c>verifyMode</c> value matching
    /// NEITHER accepted spelling, not even by case, must still be rejected
    /// with a located, actionable <c>[enum]</c> message — but must NEVER
    /// fabricate a "write '...'" suggestion, since nothing case-insensitively
    /// matches. Mirrors the identical with-hint/without-hint pairing already
    /// pinned for <c>imagePullPolicy</c> and dependency <c>type</c> in
    /// EnvironmentSchemaTests.cs.
    /// </summary>
    [Fact]
    public void Validate_VerifyModeUnrecognisedValue_IsRejectedWithNoFabricatedSuggestion()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                verifyMode: SOMETIMES
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/verifyMode" &&
            e.Message.Contains("[enum]", System.StringComparison.Ordinal) &&
            e.Message.Contains("'SOMETIMES'", System.StringComparison.Ordinal) &&
            e.Message.Contains("IMMEDIATE", System.StringComparison.Ordinal) &&
            !e.Message.Contains("write '", System.StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------
    // A1: 'timeout' de-branching (composite-branch noise, prong 1)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A malformed <c>timeout</c> value (neither a genuine duration string nor
    /// a number — the OLD two-branch <c>oneOf</c>'s <c>{string}</c>/
    /// <c>{number}</c> shapes both rejected it identically, but a BOOLEAN
    /// hits neither JSON type at all) must yield exactly one error. Chosen
    /// deliberately over an "invalid string" case: any string at all
    /// satisfies the merged <c>"type": ["string","number"]</c>'s type check
    /// (there is no format constraint on the string form), so a boolean is
    /// the only shape that still demonstrates a genuine rejection post-merge.
    /// </summary>
    [Fact]
    public void Validate_TimeoutBoolean_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                timeout: true
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        var onlyError = Assert.Single(result.Errors, e => e.InstanceLocation == "/steps/0/timeout");
        Assert.Contains("[type]", onlyError.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A valid <c>timeout</c> (either shape) must never contribute noise from
    /// the OLD two-branch <c>oneOf</c>'s non-matching branch merely because
    /// the document fails elsewhere — the pre-existing noise the brief names
    /// directly (this field's <c>oneOf</c> predates the services/dependencies
    /// closure). Now impossible by construction: a single merged
    /// <c>"type": ["string","number"]</c> schema has no second branch to leak
    /// from.
    /// </summary>
    [Theory]
    [InlineData("30s")]
    [InlineData("45")]
    public void Validate_ValidTimeout_InDocumentFailingElsewhere_NoSpuriousTypeError(string timeoutValue)
    {
        var yaml = $$"""
            steps:
              - id: s1
                type: noop.echo
                timeout: {{timeoutValue}}
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "The second step is missing 'id' and must still be rejected.");
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation == "/steps/0/timeout");
        Assert.Contains(result.Errors, e => e.InstanceLocation == "/steps/1");
    }

    // -------------------------------------------------------------------------
    // Empty or malformed YAML
    // -------------------------------------------------------------------------

    /// <summary>
    /// Passing an empty string or structurally broken YAML must return an invalid
    /// <see cref="SchemaValidationResult"/> rather than propagating an exception.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(": broken: yaml: [unclosed")]
    public void Validate_EmptyOrMalformedYaml_ReturnsInvalidNotThrow(string yaml)
    {
        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Boolean scalar: continueOnFailure must reach the validator as a JSON bool
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step with <c>continueOnFailure: true</c> (an unquoted YAML boolean)
    /// must be accepted.  Before the fix, the <see cref="YamlScalarTypeResolver"/>
    /// left booleans as strings, causing the JSON Schema <c>type: boolean</c>
    /// constraint to reject the document with "[type] Value is 'string' but
    /// should be 'boolean'".
    /// </summary>
    [Fact]
    public void Validate_ContinueOnFailureTrue_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                continueOnFailure: true
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// Likewise, <c>continueOnFailure: false</c> must be accepted.
    /// </summary>
    [Fact]
    public void Validate_ContinueOnFailureFalse_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                continueOnFailure: false
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// A <em>quoted</em> <c>"true"</c> is a YAML string; the schema constrains
    /// <c>continueOnFailure</c> to <c>type: boolean</c>, so a quoted value must
    /// be rejected.  This confirms the resolver only coerces plain scalars and
    /// does not affect quoted strings.
    /// </summary>
    [Fact]
    public void Validate_ContinueOnFailureQuotedString_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                continueOnFailure: "true"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A quoted \"true\" string must be rejected for a boolean field.");
        Assert.NotEmpty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Provider-specific extra fields (root-only schema, no provider clauses)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step with provider-specific fields (such as <c>method: GET</c> for an
    /// <c>http.rest</c> step) is now REJECTED here — the typo-closing change
    /// (see <c>SchemaStepSurfaceClosureTests</c>) replaced <c>$defs/step</c>'s
    /// old <c>additionalProperties: true</c> with <c>unevaluatedProperties:
    /// false</c>. <see cref="YamlSchemaValidator"/> evaluates the ROOT schema
    /// alone — no provider <c>allOf</c>/<c>if</c>/<c>then</c> clauses are ever
    /// spliced in here (that is <see cref="SchemaComposer"/>'s job, exercised
    /// by <see cref="DocumentValidator"/>, the actual pre-compile gate an
    /// author's suite hits — nothing under <c>src/</c> calls this validator in
    /// the real pipeline). With no clause to annotate 'method'/'path'/'expect'
    /// as evaluated, unevaluatedProperties correctly rejects them here: this
    /// root-only path can only ever validate the COMMON step fields declared
    /// directly on <c>$defs/step</c> itself.
    /// </summary>
    [Fact]
    public void Validate_ProviderSpecificExtraFields_AreRejectedWithoutProviderClauses()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                method: GET
                path: /health
                expect:
                  status: 200
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected provider-specific fields to be rejected by the root-only schema " +
            "(no provider allOf/if/then clauses to mark them evaluated).");
        Assert.NotEmpty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // M3: Step id pattern constraint
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step whose <c>id</c> contains a space (or other character outside the
    /// allowed pattern) must be rejected by the schema.  The pattern is
    /// <c>^[A-Za-z_][A-Za-z0-9_-]*$</c>.
    /// </summary>
    [Fact]
    public void Validate_IdWithIllegalCharacter_IsRejected()
    {
        const string yaml = """
            steps:
              - id: "bad id!"
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected schema validation to reject an id containing spaces and '!'.");
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// A step id that starts with a digit must be rejected (the pattern requires
    /// the first character to be a letter or underscore).
    /// </summary>
    [Fact]
    public void Validate_IdStartingWithDigit_IsRejected()
    {
        const string yaml = """
            steps:
              - id: "1step"
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected schema validation to reject an id starting with a digit.");
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// A step id that uses only letters, digits, underscores, and hyphens —
    /// and begins with a letter — must be accepted.
    /// </summary>
    [Fact]
    public void Validate_ValidIdWithHyphen_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: call-api-v2
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid id 'call-api-v2' to be accepted; errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// A step id that starts with an underscore must be accepted (underscores are
    /// valid as the leading character per the pattern).
    /// </summary>
    [Fact]
    public void Validate_IdStartingWithUnderscore_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: _internal_step
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected id '_internal_step' to be accepted; errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Phase 0 (retire-bare-aliases): step 'type' pattern constraint
    // -------------------------------------------------------------------------

    /// <summary>
    /// A bare (non-dotted) step type must be rejected by the schema's
    /// <c>pattern</c> constraint on <c>type</c>. Bare family aliases were
    /// retired pre-v1.0 (schema still unpublished): the dotted
    /// <c>family.provider</c> form is the only accepted step type.
    /// </summary>
    [Fact]
    public void Validate_BareTypeWithoutDot_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected schema validation to reject a bare (non-dotted) step type.");
        Assert.NotEmpty(result.Errors);

        // At least one error must be located at the step's 'type' property and
        // reference the 'pattern' keyword, so this test cannot pass on some
        // unrelated schema failure (e.g. a missing 'id').
        var pointsAtTypePattern = result.Errors.Any(e =>
            e.InstanceLocation.EndsWith("/type", System.StringComparison.Ordinal) &&
            e.Message.Contains("[pattern]", System.StringComparison.Ordinal));

        Assert.True(pointsAtTypePattern,
            $"Expected an error at .../type referencing the 'pattern' keyword, got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// The dotted <c>family.provider</c> form must continue to pass schema
    /// validation (the pattern requires, and this satisfies, exactly one dot
    /// separating two lowercase-alphanumeric-hyphen segments).
    /// </summary>
    [Fact]
    public void Validate_DottedType_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    // ── Publication boundary: no internal identifiers in any 'description' ─────

    /// <summary>
    /// MAJOR-3 (feat/close-remaining-surfaces, second-round gatekeeper
    /// finding): a schema node's <c>description</c> ships verbatim to the
    /// VSCode extension's hover text AND, via <c>LanguageReferenceGenerator</c>,
    /// to the published Pages site — an internal branch or task identifier
    /// there is both meaningless to a YAML author and a publication-boundary
    /// violation (CLAUDE.md: task/sprint identifiers stay out of every
    /// published surface). Maintainer rationale belongs in a sibling
    /// <c>$comment</c> (validation-inert, never read by the generator — see
    /// $defs/step's own 'capture' node for the established pattern), never in
    /// <c>description</c>. Scans every 'description' string anywhere in the
    /// FULL COMPOSED schema — root language schema AND every registered
    /// provider's own spliced fragment, not merely the root — for a
    /// branch/PR/issue-shaped token, so a future edit cannot reintroduce the
    /// same leak elsewhere undetected.
    /// </summary>
    /// <remarks>
    /// m2 (third-round gatekeeper finding): the original version of this
    /// guard read ONLY <c>SchemaResources.ReadRootLanguageSchemaJson()</c> —
    /// the static root schema — which covers 45 of the 217 descriptions the
    /// composed schema actually ships. A provider fragment (a PUBLIC
    /// extension point — Core today, Community or a customer's own tomorrow)
    /// could leak an internal identifier into its own <c>description</c> and
    /// this gate would never see it. Reading
    /// <see cref="SchemaComposer.ComposeSchemaJson"/> instead — the same
    /// composed-schema construction <c>LanguageReferenceGoldenTests</c> and
    /// <c>SchemaFreezeTests</c> already anchor their own golden files to —
    /// brings every provider fragment inside the gate. Widens coverage only:
    /// re-run against the full 25-Core-provider composed schema found zero
    /// offenders beyond the root-schema sites already fixed for MAJOR-3.
    /// </remarks>
    [Fact]
    public void NoDescriptionAnywhereInTheSchema_ContainsAnInternalIdentifier()
    {
        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());
        var schemaJson = SchemaComposer.ComposeSchemaJson(registry);
        using var doc = JsonDocument.Parse(schemaJson);

        // Matches e.g. 'feat/close-remaining-surfaces', 'fix/dependency-image-override',
        // a bare '#337', or 'PR #123' — deliberately broad rather than an exact
        // enumeration, since the whole point is to catch a NEW pattern too.
        var identifierPattern = new Regex(
            @"\b(feat|fix|chore|docs)/[a-z0-9-]+\b|(?<![A-Za-z0-9])#\d+\b",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        FindDescriptionOffenders(doc.RootElement, "", identifierPattern, offenders);

        Assert.True(offenders.Count == 0,
            "The following schema 'description' fields contain an internal "
            + $"branch/task identifier — move the rationale into a sibling '$comment' instead:{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// The Core provider assemblies that compose the v1 schema, anchored by
    /// one concrete provider type per assembly (mirrors
    /// <c>SchemaFreezeTests.CoreProviderAssemblies</c> and
    /// <c>LanguageReferenceGoldenTests.CoreProviderAssemblies</c>). Listing
    /// them by anchor type makes a renamed/removed provider a compile error
    /// here too, rather than a silently-narrower scan.
    /// </summary>
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(DbAssertSqlServerProvider).Assembly,   // db-assert.sqlserver
        typeof(DbAssertMongodbProvider).Assembly,     // db-assert.mongodb
        typeof(DbAssertMysqlProvider).Assembly,       // db-assert.mysql
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
        typeof(MailExpectSmtpProvider).Assembly,      // mail-expect.smtp
        typeof(CacheAssertRedisProvider).Assembly,    // cache-assert.redis
        typeof(MqPublishRabbitmqProvider).Assembly,   // mq-publish.rabbitmq
        typeof(MqExpectRabbitmqProvider).Assembly,    // mq-expect.rabbitmq
        typeof(MqPublishNatsProvider).Assembly,       // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,        // mq-expect.nats
        typeof(CacheAssertElasticsearchProvider).Assembly, // cache-assert.elasticsearch
        typeof(MqPublishAzureServiceBusProvider).Assembly, // mq-publish.azureservicebus
        typeof(MqExpectAzureServiceBusProvider).Assembly,  // mq-expect.azureservicebus
        typeof(MqPublishRedisProvider).Assembly,      // mq-publish.redis
        typeof(MqExpectRedisProvider).Assembly,        // mq-expect.redis
        typeof(MetricsAssertPrometheusProvider).Assembly,  // metrics-assert.prometheus
        typeof(DbAssertDynamodbProvider).Assembly,    // db-assert.dynamodb
        typeof(StorageAssertS3Provider).Assembly,     // storage-assert.s3
        typeof(TraceExpectOtlpProvider).Assembly,     // trace-expect.otlp
        typeof(HttpSoapProvider).Assembly,             // http.soap
    };

    private static void FindDescriptionOffenders(
        JsonElement element, string path, Regex identifierPattern, List<string> offenders)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "description" &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    identifierPattern.IsMatch(property.Value.GetString() ?? string.Empty))
                {
                    offenders.Add($"{path}/description");
                }

                FindDescriptionOffenders(property.Value, $"{path}/{property.Name}", identifierPattern, offenders);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                FindDescriptionOffenders(item, $"{path}/{index}", identifierPattern, offenders);
                index++;
            }
        }
    }
}
