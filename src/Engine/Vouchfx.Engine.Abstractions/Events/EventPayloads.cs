// Typed event-payload records for the vouchfx structured JSON Lines event
// stream (§14.4).
//
// Wire-format contract — FLAT shape:
//   All fields (envelope-shared + payload-specific) are siblings at the root
//   JSON object level.  There is no nested "payload" object.  This matches
//   the §14.4 examples where stepId, attempt, verdict, etc. are direct
//   siblings of type/runId/ts.
//
// Each record therefore repeats the envelope-shared fields (v, schemaVersion,
// type, ts, runId, correlationIds) with the same [JsonPropertyName] wire names
// and defaults as EventEnvelope, PLUS its own typed fields.  The type default
// is set to the matching EventTypes constant so callers never have to set it.
//
// Forward compatibility:
//   Because the wire shape is flat, a renderer that only knows EventEnvelope
//   will capture the payload-specific fields in EventEnvelope.Extra via
//   [JsonExtensionData], satisfying the §14 "renderers tolerate unknown fields"
//   guarantee.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions.Reproducibility;

namespace Vouchfx.Engine.Abstractions.Events;

// ---------------------------------------------------------------------------
// VerdictCounts — nested object inside ScenarioCompletedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Per-verdict step counts reported at scenario completion (§14.4).
/// </summary>
/// <remarks>
/// Wire keys are lower-camel: <c>pass</c>, <c>fail</c>, <c>envError</c>,
/// <c>inconclusive</c>.
/// </remarks>
public sealed record VerdictCounts
{
    /// <summary>Number of steps that passed.</summary>
    [JsonPropertyName("pass")]
    public int Pass { get; init; }

    /// <summary>Number of steps that failed their assertions.</summary>
    [JsonPropertyName("fail")]
    public int Fail { get; init; }

    /// <summary>Number of steps that terminated with an environment error.</summary>
    [JsonPropertyName("envError")]
    public int EnvError { get; init; }

    /// <summary>Number of steps whose outcome was inconclusive.</summary>
    [JsonPropertyName("inconclusive")]
    public int Inconclusive { get; init; }
}

// ---------------------------------------------------------------------------
// ScenarioStartedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a scenario begins execution (§14.4, type
/// <see cref="EventTypes.ScenarioStarted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>scenarioId</c>, <c>file</c>, and
/// <c>contentHash</c> are siblings of the envelope fields at the root JSON
/// object level.
/// </remarks>
public sealed record ScenarioStartedEvent
{
    /// <summary>
    /// Envelope schema generation.  Currently <c>1</c>.
    /// </summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Human-readable schema version string, e.g. <c>"v1"</c>.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.ScenarioStarted"/> (<c>"scenario-started"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.ScenarioStarted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifier of the run this event belongs to.
    /// </summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers (e.g. trace/span).  Omitted from the
    /// wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Unique identifier of the scenario being executed.
    /// </summary>
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    /// <summary>
    /// Path to the <c>.e2e.yaml</c> file that defines this scenario.
    /// Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("file")]
    public string? File { get; init; }

    /// <summary>
    /// SHA-256 content hash of the <c>.e2e.yaml</c> file, used to detect
    /// mutations between runs.  Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }
}

// ---------------------------------------------------------------------------
// StepStartedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a step begins its first (or only) attempt (§14.4, type
/// <see cref="EventTypes.StepStarted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>stepId</c>, <c>kind</c>, <c>verifyMode</c>, and
/// <c>timeoutMs</c> are siblings of the envelope fields.
/// </remarks>
public sealed record StepStartedEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.StepStarted"/> (<c>"step-started"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.StepStarted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>Unique identifier of the step within its scenario.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>
    /// Step-type discriminator, e.g. <c>"http.rest"</c>,
    /// <c>"mq-publish.kafka"</c>.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    /// Verify mode (<c>"IMMEDIATE"</c> or <c>"RETRY"</c>).  Omitted from
    /// the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("verifyMode")]
    public string? VerifyMode { get; init; }

    /// <summary>
    /// Step-level timeout in milliseconds.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("timeoutMs")]
    public long? TimeoutMs { get; init; }
}

// ---------------------------------------------------------------------------
// StepAttemptEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted for every individual attempt of a step (§14.4, type
/// <see cref="EventTypes.StepAttempt"/>).
/// </summary>
/// <remarks>
/// RETRY steps emit one <c>step-attempt</c> per polling cycle, making the
/// polling timeline renderable without re-running the suite (§14.5).
/// Wire shape is flat: <c>stepId</c>, <c>attempt</c>, <c>tMs</c>,
/// <c>outcome</c>, and <c>observation</c> are siblings of the envelope fields.
/// </remarks>
public sealed record StepAttemptEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.StepAttempt"/> (<c>"step-attempt"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.StepAttempt;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>Unique identifier of the step within its scenario.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>
    /// One-based attempt counter.  The first attempt is <c>1</c>.
    /// </summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    /// <summary>
    /// Elapsed wall-clock time for this attempt in milliseconds.
    /// </summary>
    [JsonPropertyName("tMs")]
    public long TMs { get; init; }

    /// <summary>
    /// Outcome of this individual attempt.  <see langword="null"/> when the
    /// engine has not yet resolved an outcome (e.g. a mid-RETRY poll).
    /// Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("outcome")]
    public Verdict? Outcome { get; init; }

    /// <summary>
    /// Provider-supplied observation data (diff, matched count, raw response,
    /// etc.) captured during this attempt.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("observation")]
    public JsonElement? Observation { get; init; }
}

// ---------------------------------------------------------------------------
// CapturedVar + SubstitutionRef — provenance metadata for G-01
// ---------------------------------------------------------------------------

/// <summary>
/// Records the provenance of a single JSONPath capture operation that was
/// declared in a step's <c>capture</c> block (S04-G-01, DSL §3).
/// </summary>
/// <remarks>
/// <para>
/// This record carries only metadata — the captured VALUE is deliberately
/// absent.  This is secret-safe by construction (§17): a captured value may
/// derive from a secret reference and must never appear in the event stream.
/// </para>
/// </remarks>
/// <param name="Name">
/// The author-supplied variable name declared in the YAML <c>capture</c> block
/// (e.g. <c>"orderId"</c>).
/// </param>
/// <param name="Path">
/// The JSONPath expression used to extract the value from the step's response
/// body (e.g. <c>"$.id"</c>).
/// </param>
/// <param name="Matched">
/// <see langword="true"/> when the JSONPath expression matched at least one
/// node in the response body; <see langword="false"/> when the expression
/// yielded no match (which also sets the step verdict to
/// <c>Inconclusive</c> with reason <c>upstream-capture-unmet</c>).
/// </param>
public sealed record CapturedVar(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("matched")] bool Matched);

/// <summary>
/// Records the provenance of a single <c>{placeholder}</c> substitution that
/// was detected at compile time in a substitutable field of a step
/// (S04-G-01, DSL §3, S04-B-03).
/// </summary>
/// <remarks>
/// <para>
/// Provenance is derived <em>at compile time</em> from the field text (which
/// placeholder names appear) — not at runtime — so no value ever flows into
/// this record.  This is secret-safe by construction (§17).
/// </para>
/// </remarks>
/// <param name="Placeholder">
/// For a plain placeholder, the placeholder name as it appeared in the template
/// (e.g. <c>"orderId"</c> for the token <c>{orderId}</c>).  For a secret-derived
/// entry (<see cref="SecretDerived"/> is <see langword="true"/>) this is instead
/// the non-sensitive secret <em>reference</em> label <c>"{source}/{path}"</c>
/// (e.g. <c>"env/API_TOKEN"</c>) — the reference is intentionally shown in reports
/// (§17, docs/02 §14.5); the resolved value is never present.
/// </param>
/// <param name="OriginStepId">
/// The step identifier that first captured the variable (i.e. the step whose
/// <c>capture</c> map declares <paramref name="Placeholder"/> as a key).
/// <see langword="null"/> when the variable originates from the <c>variables</c>
/// block, when the entry is secret-derived (a secret does not originate from a
/// prior capture), or when it is otherwise not traceable to a prior capture.
/// </param>
/// <param name="SecretDerived">
/// <see langword="true"/> when this entry records a <c>${secret:source/path}</c>
/// reference found in a substitutable field of the step (S05-G-01); in that case
/// <see cref="Placeholder"/> carries the reference label, never the value (§17).
/// <see langword="false"/> for an ordinary <c>{placeholder}</c> token: whether a
/// placeholder's runtime value happens to derive from a secret is not determinable
/// at compile time in the general case, so a plain placeholder is never
/// speculatively tainted.
/// </param>
public sealed record SubstitutionRef(
    [property: System.Text.Json.Serialization.JsonPropertyName("placeholder")] string Placeholder,
    [property: System.Text.Json.Serialization.JsonPropertyName("originStepId")] string? OriginStepId,
    [property: System.Text.Json.Serialization.JsonPropertyName("secretDerived")] bool SecretDerived);

// ---------------------------------------------------------------------------
// StepCompletedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a step is fully resolved — by success, exhausted retries,
/// timeout, or environment error (§14.4, type
/// <see cref="EventTypes.StepCompleted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>stepId</c>, <c>verdict</c>, and <c>durationMs</c>
/// are siblings of the envelope fields.
/// </remarks>
public sealed record StepCompletedEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.StepCompleted"/> (<c>"step-completed"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.StepCompleted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>Unique identifier of the step within its scenario.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>
    /// Final verdict for the step (§12.1).  Serialised to its canonical wire
    /// token via <see cref="VerdictJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("verdict")]
    public required Verdict Verdict { get; init; }

    /// <summary>
    /// Total wall-clock duration of all attempts combined, in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public required long DurationMs { get; init; }

    /// <summary>
    /// Per-capture provenance records for this step (S04-G-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry per element in the step's <c>capture</c> block.
    /// <see langword="null"/> (and omitted from the wire) when the step
    /// declares no <c>capture</c> entries.
    /// </para>
    /// <para>
    /// No captured VALUE is ever included — this is secret-safe by construction (§17).
    /// </para>
    /// <para>
    /// Renderers that do not understand this field will ignore it via
    /// <c>[JsonExtensionData]</c> on <see cref="EventEnvelope"/> (§14 forward-compat).
    /// </para>
    /// </remarks>
    [JsonPropertyName("captured")]
    public IReadOnlyList<CapturedVar>? Captured { get; init; }

    /// <summary>
    /// Compile-time substitution provenance records for this step (S04-G-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry per distinct <c>{placeholder}</c> token found in any
    /// substitutable field of the step at compile time.
    /// <see langword="null"/> (and omitted from the wire) when no substitutable
    /// fields contain placeholders.
    /// </para>
    /// <para>
    /// No runtime VALUE is ever included — provenance is derived at compile time
    /// and is secret-safe by construction (§17).
    /// </para>
    /// <para>
    /// Renderers that do not understand this field will ignore it via
    /// <c>[JsonExtensionData]</c> on <see cref="EventEnvelope"/> (§14 forward-compat).
    /// </para>
    /// </remarks>
    [JsonPropertyName("substitutions")]
    public IReadOnlyList<SubstitutionRef>? Substitutions { get; init; }

    /// <summary>
    /// Provider-supplied structured observation for this step (S07-G-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries the step's <c>StepOutcome.Observation</c> as a structured
    /// <see cref="JsonElement"/> (e.g. a failed-assertion diff such as
    /// <c>{"column":"status","expected":"SHIPPED","actual":"PENDING"}</c>), so that a
    /// renderer can compute an expected-vs-observed diff at <em>render time</em> via an
    /// <c>IStepDiffRenderer</c> looked up by step kind.  <see langword="null"/> (and
    /// omitted from the wire) when the step recorded no observation.
    /// </para>
    /// <para>
    /// This field is <strong>structured data only</strong> — no rendered diff text is
    /// ever stored here, preserving the §14 invariant that one schema-versioned stream
    /// feeds every renderer.  Renderers that do not understand it ignore it via
    /// <c>[JsonExtensionData]</c> on <see cref="EventEnvelope"/> (§14 forward-compat).
    /// </para>
    /// </remarks>
    [JsonPropertyName("observation")]
    public JsonElement? Observation { get; init; }
}

// ---------------------------------------------------------------------------
// ScenarioCompletedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a scenario finishes execution (§14.4, type
/// <see cref="EventTypes.ScenarioCompleted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>scenarioId</c>, <c>verdict</c>, and <c>counts</c>
/// are siblings of the envelope fields.  <c>counts</c> is a nested object
/// with its own wire keys (<c>pass</c>, <c>fail</c>, <c>envError</c>,
/// <c>inconclusive</c>).
/// </remarks>
public sealed record ScenarioCompletedEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.ScenarioCompleted"/> (<c>"scenario-completed"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.ScenarioCompleted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Unique identifier of the scenario that completed.
    /// </summary>
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    /// <summary>
    /// Aggregate verdict for the scenario (§12.1).  Serialised to its
    /// canonical wire token via <see cref="VerdictJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("verdict")]
    public required Verdict Verdict { get; init; }

    /// <summary>
    /// Per-verdict step counts for this scenario.
    /// </summary>
    [JsonPropertyName("counts")]
    public required VerdictCounts Counts { get; init; }

    /// <summary>
    /// The scenario-level cause, when the engine has one to give: a schema rejection, a
    /// secret-reference failure, a security preflight refusal, a suite-level abort stamped onto
    /// every scenario it affected. Omitted from the wire when <see langword="null"/>, which is
    /// every ordinary pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Additive, optional, and a deliberate change to the frozen v1 event-wire contract
    /// (#372).</strong> The freeze forbids renaming a property, changing a CLR type, or changing a
    /// <c>[JsonPropertyName]</c> — all of which break every consumer. It does not forbid adding an
    /// optional field: §14 requires renderers to tolerate unknown fields, so an older consumer
    /// reading a stream carrying this simply does not see it, and a stream with nothing to report
    /// is byte-identical to before because <c>null</c> is omitted. The golden gate exists to make
    /// such a change deliberate rather than to forbid it, and its line was regenerated through the
    /// documented flag with the one-line diff reviewed.
    /// </para>
    /// <para>
    /// <strong>Why the artefacts needed it.</strong> Before this, no written channel carried a
    /// scenario-level cause at all: <c>EarlyMessage</c> had exactly one consumer — the terminal —
    /// while JUnit's message was built from the scenario id, verdict token and counts, and the
    /// HTML renderer read a message only from <c>environment-error</c> events. A maintainer
    /// triaging from a JUnit publisher UI — the artefact existing precisely so they need not read
    /// console logs — saw <c>Scenario 'a' INCONCLUSIVE (pass=0 fail=0 …)</c> and could not tell a
    /// suite the engine REJECTED from one whose scenarios were legitimately skipped.
    /// </para>
    /// <para>
    /// <strong>This is not the <c>scenarioId</c>-on-step-events precedent.</strong> That one was
    /// frozen OUT deliberately and must stay out: the renderer's <c>(runId,stepId)</c> cache
    /// already disambiguates aggregated streams, so it was redundant. This field is not
    /// obtainable from anything else on the wire.
    /// </para>
    /// <para>
    /// <strong>A written channel, and what is and is not guaranteed about it.</strong> This text
    /// is archived in the event stream, the JUnit <c>message</c> attribute and the HTML report, so
    /// two things must never reach it: a resolved secret value, and an absolute host path.
    /// </para>
    /// <para>
    /// The SECRET half is now discharged by the engine, structurally rather than by convention:
    /// every <c>ScenarioCompletedEvent</c> <c>Vouchfx.Engine.Runtime</c> emits is constructed in
    /// one place,
    /// <c>Vouchfx.Engine.Runtime.StepEventBuilder.ScenarioCompletedLine</c>, which scrubs the
    /// message through the <c>ResolvedSecretLedger</c> its caller hands it. That ledger is a
    /// REQUIRED parameter, so a producer holding one cannot omit the scrub by forgetting, and a
    /// producer with none writes <c>null</c> on purpose. A source-scanning CI gate
    /// (<c>SecretObservationLeakPenetrationTests
    /// .EveryScenarioCompletedEmission_InRuntime_GoesThroughTheStampingChokepoint</c>) keeps it
    /// the only construction site. This replaced a per-producer obligation that was measured
    /// half-kept: <c>RunSuiteAsync</c>'s <c>OrchestrationException</c> catch scrubbed and its
    /// <c>ArgumentException</c> catch did not.
    /// </para>
    /// <para>
    /// The PATH half is not, and cannot be: nothing covers a filesystem path, and no scrubber can
    /// remove one after the fact. It stays the producer's obligation, pinned for the one shape
    /// that has been measured leaking by
    /// <c>SecurityDiagnosticPathDisclosureTests</c>.
    /// </para>
    /// </remarks>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

// ---------------------------------------------------------------------------
// ReproducibilityEnvelopeEvent — S05-B-03 (§17, docs/02 §3.2.2)
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted once per scenario carrying the reproducibility envelope (§14.4, type
/// <see cref="EventTypes.ReproducibilityEnvelope"/>).
/// </summary>
/// <remarks>
/// <para>
/// Wire shape is flat: <c>scenarioId</c>, <c>envSchemaVersion</c>,
/// <c>secretReferences</c>, and <c>fixtures</c> are siblings of the envelope
/// fields.  The two arrays are the envelope's payload (see
/// <see cref="ReproducibilityEnvelope"/>).
/// </para>
/// <para>
/// <strong>Secret-safe by construction (§17):</strong> the envelope is built from
/// reference text and fixture content only — the secret resolver is never invoked
/// — so no resolved secret value can appear in this event.  The terminal renderer
/// does not yet have a case for this type and therefore silently ignores it via
/// its <c>default:</c> branch (§14 forward-compatibility); the envelope is for the
/// JSON Lines consumers (reproducibility diffing, the Healer).
/// </para>
/// <para>
/// The envelope's own schema version is carried as <c>envSchemaVersion</c> to keep
/// it distinct from the event-stream <c>schemaVersion</c> (the two version
/// independently): the former versions the envelope payload, the latter versions
/// the event envelope.
/// </para>
/// </remarks>
public sealed record ReproducibilityEnvelopeEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable event-stream schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.ReproducibilityEnvelope"/>
    /// (<c>"reproducibility-envelope"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.ReproducibilityEnvelope;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Unique identifier of the scenario this envelope describes.
    /// </summary>
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    /// <summary>
    /// The envelope's own schema version (e.g.
    /// <see cref="ReproducibilityEnvelope.CurrentSchemaVersion"/>), distinct from
    /// the event-stream <see cref="SchemaVersion"/>.
    /// </summary>
    [JsonPropertyName("envSchemaVersion")]
    public required string EnvSchemaVersion { get; init; }

    /// <summary>
    /// The distinct secret-reference digests for the scenario.  Each entry carries
    /// a non-sensitive source id and the SHA-256 of the verbatim reference token —
    /// never the resolved value (§17).
    /// </summary>
    [JsonPropertyName("secretReferences")]
    public required IReadOnlyList<SecretReferenceDigest> SecretReferences { get; init; }

    /// <summary>
    /// The content-hash digests for every applied seed fixture (docs/02 §3.2.2).
    /// </summary>
    [JsonPropertyName("fixtures")]
    public required IReadOnlyList<FixtureDigest> Fixtures { get; init; }
}

// ---------------------------------------------------------------------------
// TransportNoticeKinds — the closed `kind` vocabulary of TransportNoticeEvent
// ---------------------------------------------------------------------------

/// <summary>
/// The closed vocabulary of <see cref="TransportNoticeEvent.Kind"/> values (§14.4,
/// type <see cref="EventTypes.TransportNotice"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Spelled exactly once, here.</strong>  Both advisories are produced by
/// different call sites and consumed by different renderers; a string literal at
/// each producer is precisely how the two notices came to be treated differently
/// in the first place.  Every producer and every consumer references these
/// constants.
/// </para>
/// <para>
/// <strong>Why a string and not an enum.</strong>  <see cref="Verdict"/> is an enum
/// with a converter on this same frozen wire, so the enum is the house precedent and
/// the departure needs a reason.  The reason is round-trip fidelity of an unknown
/// token: a stream from a newer engine carrying a third advisory must deserialise,
/// retain the token exactly as it arrived, and re-serialise unchanged.  An enum would
/// need an <c>Unknown</c> member — permanently meaningless, which is the shape this
/// record's design deliberately rejects — plus a side channel to keep the original
/// text.  A string needs neither.  Pinned by
/// <c>TransportNoticeEventTests.UnknownKind_IsCarriedVerbatim_NotRejected</c>.
/// </para>
/// <para>
/// Matching is <em>ordinal</em>.  These are wire tokens, not display text: a
/// culture-sensitive or case-insensitive comparison would accept a token the
/// engine never emits.  The C# constant patterns used by <see cref="IsKnown"/>
/// compare ordinally by construction.
/// </para>
/// <para>
/// The vocabulary is closed but not closed-at-two: a third transport advisory is
/// plausible, and adding a constant here is the additive change that absorbs it.
/// A consumer meeting an unrecognised <c>kind</c> must not reject the record and must
/// not fail the parse — but §14 asks for <em>tolerance</em>, and tolerance is not
/// discard.  Surface it as an unrecognised transport advisory, naming its service and
/// its selected endpoint.  Dropping it silently would make a newer engine's third
/// advisory invisible to an older consumer, which is exactly the invisibility this
/// record exists to end, displaced one engine version forward.
/// </para>
/// <para>
/// <strong>That tolerance covers the token vocabulary, not the field set.</strong>
/// <see cref="TransportNoticeEvent.Kind"/>, <see cref="TransportNoticeEvent.Service"/>,
/// <see cref="TransportNoticeEvent.SelectedEndpoint"/> and
/// <see cref="TransportNoticeEvent.RunId"/> are <c>required</c>, so a line missing any
/// of them throws <c>JsonException</c> rather than degrading — a future advisory that
/// is not endpoint-scoped cannot simply reuse this record as it stands.  Relaxing one
/// of those <c>required</c> markers later is itself wire-compatible (no producer stops
/// writing the field, no consumer's parse changes) and costs only a golden
/// regeneration; it is a deliberate act rather than an additive one.
/// </para>
/// </remarks>
public static class TransportNoticeKinds
{
    /// <summary>
    /// The engine selected a plaintext listener for a targeted service while an
    /// https listener was also available.  Events of this kind carry
    /// <see cref="TransportNoticeEvent.RejectedEndpoint"/>.
    /// </summary>
    public const string PlaintextDowngrade = "plaintext-downgrade";

    /// <summary>
    /// The run addresses an https listener for which the engine configures no client
    /// trust material of its own.  <strong>What is absent is engine-configured trust,
    /// not verification</strong> — with no <c>security</c> block the platform's own
    /// trust store still validates the chain, full depth, exactly as it does for any
    /// other .NET HTTPS request; what the engine does not do is contribute a private
    /// anchor, pin the peer or present a client identity.  The token says that, and
    /// only that, because on this wire the token is the entire payload a machine
    /// consumer receives.  Events of this kind carry no rejected endpoint — nothing
    /// was rejected — so <see cref="TransportNoticeEvent.RejectedEndpoint"/> is absent
    /// from the wire.
    /// </summary>
    public const string NoEngineTrust = "no-engine-trust";

    /// <summary>
    /// True when <paramref name="kind"/> is a token this engine version knows.
    /// A <see langword="false"/> result on a stream from a newer engine means
    /// "not understood here", not "invalid".
    /// </summary>
    /// <param name="kind">The <c>kind</c> token read from the wire.</param>
    public static bool IsKnown(string? kind) =>
        kind is PlaintextDowngrade or NoEngineTrust;
}

// ---------------------------------------------------------------------------
// TransportNoticeEvent — #450 / #453
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when the engine has a transport advisory about the endpoint a targeted
/// service is addressed on (§14.4, type <see cref="EventTypes.TransportNotice"/>).
/// </summary>
/// <remarks>
/// <para>
/// Wire shape is flat: <c>kind</c>, <c>service</c>, <c>selectedEndpoint</c>,
/// <c>rejectedEndpoint</c> and <c>replayed</c> are siblings of the envelope fields at
/// the root JSON object level, like every other record in this file.
/// </para>
/// <para>
/// <strong>Run-level, not scenario-level.</strong>  Unlike the six records above
/// it, this one carries no <c>scenarioId</c>: the advisory is a property of the
/// topology a run built, and one topology can serve many scenarios.  The service
/// name is its correlation key; see <see cref="TransportNoticeEvent.RunId"/> for
/// why the envelope's run id is not one.
/// </para>
/// <para>
/// <strong>Structured fields, never the rendered sentence.</strong>  The terminal
/// owns the wording; a consumer reconstructs meaning from <see cref="Kind"/> and
/// the two endpoint names.  Putting the sentence on the wire would freeze prose
/// that is deliberately free to be reworded.
/// </para>
/// <para>
/// <strong>Additive to the frozen v1 event-wire contract.</strong>  The freeze
/// forbids renaming a property, changing a CLR type, or changing a
/// <c>[JsonPropertyName]</c>; it does not forbid adding a record, and §14 requires
/// renderers to tolerate what they do not recognise (<c>TerminalRenderer</c>'s
/// default branch already ignores this type, deliberately — the terminal already
/// prints the advisory by its own route).  A run with no advisory to report emits
/// no record at all, so such a stream is byte-identical to one from before this
/// record existed.
/// </para>
/// </remarks>
public sealed record TransportNoticeEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.TransportNotice"/> (<c>"transport-notice"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.TransportNotice;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Envelope run identifier.  <strong>Do not join on it: on this record it may
    /// resolve to no scenario at all.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The advisory belongs to a TOPOLOGY, and one topology can serve many
    /// scenarios.  Where the topology is a single scenario's own, the producer
    /// passes that scenario's run id and the join works.  Where one topology serves
    /// a whole suite, every scenario has its own distinct run id and none of them
    /// is the topology's, so the producer mints an id belonging to nothing rather
    /// than picking one arbitrarily — attributing a topology-wide fact to one named
    /// test case would make a renderer display a false statement about that test,
    /// whereas an id that joins to nothing is merely uninformative.
    /// </para>
    /// <para>
    /// <see cref="Service"/> is the correlation key a consumer actually wants, and
    /// is why the record carries it.
    /// </para>
    /// </remarks>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// This is the slot designed for a "belongs to suite X" attribution, and the
    /// producer deliberately leaves it <see langword="null"/>: no suite-level
    /// identifier travels on this stream today, so there is nothing to put here that
    /// a consumer could join on.  If one is ever minted it belongs here, not in
    /// <see cref="RunId"/>.
    /// </remarks>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Which advisory this is — one of the <see cref="TransportNoticeKinds"/>
    /// tokens, matched ordinally.  Required: there is no default that could be
    /// right, so a producer that forgets it fails to compile rather than emitting
    /// an unattributable notice.
    /// </summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// The name of the service the advisory concerns, as declared under
    /// <c>environment.services</c>.
    /// </summary>
    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>
    /// The name of the endpoint steps addressing the service will use.
    /// </summary>
    [JsonPropertyName("selectedEndpoint")]
    public required string SelectedEndpoint { get; init; }

    /// <summary>
    /// The name of the endpoint that was available and not selected.  Present only
    /// for <see cref="TransportNoticeKinds.PlaintextDowngrade"/>; for
    /// <see cref="TransportNoticeKinds.NoEngineTrust"/> nothing was rejected, so
    /// this is <see langword="null"/> and — because the shared serialiser sets
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> — absent from the wire
    /// without needing a <c>[JsonIgnore]</c>.
    /// </summary>
    [JsonPropertyName("rejectedEndpoint")]
    public string? RejectedEndpoint { get; init; }

    /// <summary>
    /// <see langword="true"/> when this record replays an advisory raised by an
    /// earlier topology build rather than reporting a fresh one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>--watch</c> loop re-reports both advisories against a topology it kept,
    /// under a terminal qualifier saying they may be stale: the endpoint was selected
    /// once, when that topology was built, and a <c>project:</c>-form service's
    /// endpoints come from its launch profile, which is not part of the
    /// <c>environment</c> block — so editing one does not rebuild the topology.  An
    /// author can therefore change the listeners and keep being told about a transport
    /// condition that no longer holds.  That qualification cannot travel on an
    /// unqualified record, so it travels here.
    /// </para>
    /// <para>
    /// <strong>Nullable, not a defaulted <c>bool</c>.</strong>  The fresh-build paths
    /// must leave <c>replayed</c> off the wire entirely, not write
    /// <c>"replayed":false</c> on every record; the shared serialiser's
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> omits a null and would write a
    /// <see langword="false"/>.  Read an absent field as "not a replay".
    /// </para>
    /// </remarks>
    [JsonPropertyName("replayed")]
    public bool? Replayed { get; init; }
}
