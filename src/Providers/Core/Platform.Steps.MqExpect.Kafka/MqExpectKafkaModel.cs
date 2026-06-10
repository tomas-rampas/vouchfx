// Platform.Steps.MqExpect.Kafka — mq-expect.kafka step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
//
// PLAIN-payload slice (string / JSON matching). Avro / schema-registry decoding is a
// SEPARATE later task and is intentionally ABSENT from these records.  The
// KafkaMatch record is shaped so an optional schema-aware criterion could be appended
// without churning callers — but it is NOT added here.
using Platform.Sdk;

namespace Platform.Steps.MqExpect.Kafka;

/// <summary>
/// Strongly-typed model for the <c>mq-expect.kafka</c> step kind (DSL §5).
/// </summary>
/// <remarks>
/// <para>
/// This is the PLAIN-payload model: the message value is a UTF-8 string (a literal
/// value or an inline JSON document) and the assertion criteria are expressed against
/// that string.  Avro / schema-registry decoding is a separate later task and is
/// deliberately not represented here.
/// </para>
/// <para>
/// This provider is the primary <c>verifyMode: RETRY</c> consumer: the engine wraps a
/// RETRY step in a polling loop, so the emitted assertion is an IDEMPOTENT single poll.
/// It writes a non-<c>Pass</c> <see cref="Platform.Engine.Abstractions.StepOutcome"/>
/// when no matching message has been seen yet — it contains NO retry logic itself.
/// </para>
/// <para>
/// The <see cref="KafkaMatch.Key"/>, <see cref="KafkaMatch.PayloadContains"/>, and each
/// value in <see cref="KafkaMatch.Headers"/> / <see cref="KafkaMatch.Json"/> support
/// <c>{placeholder}</c> substitution and <c>${secret:source/path}</c> references; both
/// are resolved at step-execution time inside the emitted helper's guarded region
/// (§17), never at compile time.
/// </para>
/// </remarks>
/// <param name="Target">
/// Logical name of the kafka dependency to consume from, as declared under
/// <c>environment.dependencies</c>.  Resolved to a bootstrap-servers connection string
/// by the orchestrator at execution time.
/// </param>
/// <param name="Topic">
/// The Kafka topic to consume the message from.
/// </param>
/// <param name="Match">
/// The assertion criteria a consumed message must satisfy.  At least one criterion
/// (<see cref="KafkaMatch.Key"/>, a non-empty <see cref="KafkaMatch.Headers"/>,
/// <see cref="KafkaMatch.PayloadContains"/>, or a non-empty <see cref="KafkaMatch.Json"/>)
/// must be declared; an empty match is rejected by validation.
/// </param>
public sealed record MqExpectKafkaModel(
    string Target,
    string Topic,
    KafkaMatch Match) : IStepModel;

/// <summary>
/// The criteria a consumed Kafka message must satisfy for the
/// <c>mq-expect.kafka</c> assertion to pass (DSL §5).
/// </summary>
/// <remarks>
/// <para>
/// All criteria are conjunctive (logical AND): a message matches only when every
/// declared criterion is satisfied.  Absent (<see langword="null"/> or empty) criteria
/// impose no constraint.
/// </para>
/// <para>
/// PLAIN-payload slice: <see cref="PayloadContains"/> is a substring assertion over the
/// UTF-8 message value, and <see cref="Json"/> evaluates JSONPath expressions over the
/// message value parsed as JSON.  A non-JSON payload simply fails the JSON criteria
/// (it never raises an error).  Avro decoding is a separate later task.
/// </para>
/// </remarks>
/// <param name="Key">
/// Optional expected message key.  When set, a matching message's key must equal this
/// value (ordinal string equality).  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Headers">
/// Optional map of expected header names to their expected string values.  When
/// non-empty, every named header must be present on the message and its UTF-8 value
/// must equal the expected value.  Each expected value may contain <c>{placeholder}</c>
/// and <c>${secret:source/path}</c> tokens resolved at runtime; header names are used
/// verbatim.
/// </param>
/// <param name="PayloadContains">
/// Optional substring that the UTF-8 message value must contain (ordinal).  May contain
/// <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Json">
/// Optional map of JSONPath expressions to their expected string values.  When
/// non-empty, the message value is parsed as JSON and each JSONPath must select a node
/// whose stringified value equals the expected value.  A non-JSON payload or a path that
/// selects nothing fails the criterion (no error is raised).  Each expected value may
/// contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at
/// runtime; the JSONPath expressions themselves are used verbatim.
/// </param>
public sealed record KafkaMatch(
    string? Key,
    IReadOnlyDictionary<string, string>? Headers,
    string? PayloadContains,
    IReadOnlyDictionary<string, string>? Json);
