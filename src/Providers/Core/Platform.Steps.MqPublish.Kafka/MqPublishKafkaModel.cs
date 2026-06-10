// Platform.Steps.MqPublish.Kafka — mq-publish.kafka step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
//
// PLAIN-payload slice (string / JSON payloads). An Avro field is intentionally
// ABSENT from this record: schema-registry / Avro encoding is a separate later
// task.  The record is shaped so an optional `Avro` field can be appended without
// churning callers (it would be a new optional trailing parameter, defaulting to
// null) — but it is NOT added here.
using Platform.Sdk;

namespace Platform.Steps.MqPublish.Kafka;

/// <summary>
/// Strongly-typed model for the <c>mq-publish.kafka</c> step kind (DSL §5).
/// </summary>
/// <remarks>
/// <para>
/// This is the PLAIN-payload model: the <see cref="Payload"/> is a UTF-8 string
/// (a literal value or a JSON document authored inline).  Avro / schema-registry
/// encoding is a separate later task and is deliberately not represented here; the
/// record may gain an optional trailing <c>Avro</c> field in a future slice without
/// affecting existing call sites.
/// </para>
/// <para>
/// The <see cref="Topic"/>, <see cref="Key"/>, <see cref="Payload"/>, and each
/// <see cref="Headers"/> value support <c>{placeholder}</c> substitution and
/// <c>${secret:source/path}</c> references; both are resolved at step-execution
/// time inside the emitted helper's guarded region (§17), never at compile time.
/// </para>
/// </remarks>
/// <param name="Target">
/// Logical name of the kafka dependency to publish to, as declared under
/// <c>environment.dependencies</c>.  Resolved to a bootstrap-servers connection
/// string by the orchestrator at execution time.
/// </param>
/// <param name="Topic">
/// The Kafka topic to publish the message to.  May contain <c>{placeholder}</c>
/// and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Key">
/// The optional message key.  <see langword="null"/> when the step does not set a
/// key (an empty key is then sent).  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Payload">
/// The message payload, sent as the Kafka message value.  A UTF-8 string — a
/// literal value or an inline JSON document.  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Headers">
/// An optional map of message-header names to their string values.  Each value may
/// contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at
/// runtime; header names are used verbatim.  <see langword="null"/> when the step
/// declares no headers.
/// </param>
public sealed record MqPublishKafkaModel(
    string Target,
    string Topic,
    string? Key,
    string Payload,
    IReadOnlyDictionary<string, string>? Headers) : IStepModel;
