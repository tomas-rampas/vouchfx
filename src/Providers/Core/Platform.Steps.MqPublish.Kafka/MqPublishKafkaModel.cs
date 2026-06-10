// Platform.Steps.MqPublish.Kafka — mq-publish.kafka step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
//
// Two payload paths:
//   • PLAIN (Avro == null): the Payload is a UTF-8 string (literal or inline JSON)
//     produced via a string-valued Confluent.Kafka producer.  This path is unchanged
//     from the committed slice.
//   • AVRO (Avro != null): the message value is built as an Avro GenericRecord from the
//     inline schema + field map and produced via the Confluent Schema Registry Avro
//     serializer.  When Avro is set, the plain Payload is ignored.  This is an ADDITIVE
//     trailing field; the plain path is identical when it is null (§13 additive-model rule).
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
/// <param name="Avro">
/// The optional Avro / schema-registry encoding spec.  <see langword="null"/> selects the
/// PLAIN-payload path (the <see cref="Payload"/> string is produced verbatim).  When
/// non-<see langword="null"/>, the message value is built as an Avro
/// <c>GenericRecord</c> from <see cref="KafkaAvro.Schema"/> + <see cref="KafkaAvro.Record"/>
/// and produced via the Confluent Schema Registry Avro serializer, and the plain
/// <see cref="Payload"/> is ignored.  Additive trailing field (§13).
/// </param>
public sealed record MqPublishKafkaModel(
    string Target,
    string Topic,
    string? Key,
    string Payload,
    IReadOnlyDictionary<string, string>? Headers,
    KafkaAvro? Avro = null) : IStepModel;

/// <summary>
/// The Avro / schema-registry encoding spec for a <c>mq-publish.kafka</c> step
/// (DSL §5, §13).  Present (non-<see langword="null"/> on the model) only when the
/// author declares an <c>avro</c> block; its presence switches the provider from the
/// PLAIN string path to the Avro <c>GenericRecord</c> path.
/// </summary>
/// <remarks>
/// <para>
/// The serializer auto-registers <see cref="Schema"/> under <see cref="Subject"/> with the
/// registry resolved from <see cref="SchemaRegistryTarget"/> (the kafka dependency whose
/// <c>schemaRegistry: true</c> Extra provisioned the registry).  That auto-registration is
/// the "registry-validated" acceptance criterion for the published message.
/// </para>
/// <para>
/// Each value in <see cref="Record"/> supports <c>{placeholder}</c> substitution and
/// <c>${secret:source/path}</c> references, resolved at step-execution time inside the
/// emitted helper's guarded region (§17); the field <em>names</em> are used verbatim.
/// </para>
/// </remarks>
/// <param name="SchemaRegistryTarget">
/// Logical name of the kafka dependency whose schema registry this step publishes through.
/// The registry URL is staged by the orchestrator under <c>svc::&lt;target&gt;-sr</c> and
/// read at execution time via <c>VarKeys.Service(SchemaRegistryTarget + "-sr")</c>.
/// </param>
/// <param name="Subject">
/// The schema-registry subject under which the inline <see cref="Schema"/> is registered.
/// Must be non-empty for the Avro path.
/// </param>
/// <param name="Schema">
/// The inline Avro schema as avsc JSON.  Parsed at execution time via
/// <c>Avro.Schema.Parse</c>; must be a record schema.  Must be non-empty for the Avro path.
/// </param>
/// <param name="Record">
/// The fieldName → value map used to build the Avro <c>GenericRecord</c>.  Values are
/// substitutable / secret-resolvable; each is coerced from its resolved string to the
/// field's primitive type at execution time.  Must be non-empty for the Avro path.
/// </param>
public sealed record KafkaAvro(
    string SchemaRegistryTarget,
    string Subject,
    string Schema,
    IReadOnlyDictionary<string, string> Record);
