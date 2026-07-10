// Vouchfx.Steps.MqExpect.Kafka — mq-expect.kafka step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
//
// Two consume paths:
//   • PLAIN (Avro == null): the message value is a UTF-8 string and the match criteria are
//     evaluated against that string.  This path is unchanged from the committed slice.
//   • AVRO (Avro != null): the consumed message is Avro-decoded to a GenericRecord, converted
//     to a JSON string, and the EXISTING match criteria run against that JSON string — so
//     key/headers/payloadContains/json criteria all work unchanged against the decoded JSON.
//     Avro is an ADDITIVE trailing field; the plain path is identical when it is null.
using Vouchfx.Sdk;

namespace Vouchfx.Steps.MqExpect.Kafka;

/// <summary>
/// Strongly-typed model for the <c>mq-expect.kafka</c> step kind (DSL §5).
/// </summary>
/// <remarks>
/// <para>
/// The model supports two consume paths: PLAIN (when <see cref="Avro"/> is <see langword="null"/>,
/// the message value is a UTF-8 string and assertion criteria run against it directly) and
/// AVRO (when <see cref="Avro"/> is non-<see langword="null"/>, each message is Avro-decoded
/// to a <c>GenericRecord</c>, converted to a JSON string, and the same assertion criteria
/// run against that JSON string).  The optional <see cref="Avro"/> field is an additive
/// trailing field that does not affect existing call sites when <see langword="null"/>.
/// </para>
/// <para>
/// This provider is the primary <c>verifyMode: RETRY</c> consumer: the engine wraps a
/// RETRY step in a polling loop, so the emitted assertion is an IDEMPOTENT single poll.
/// It writes a non-<c>Pass</c> <see cref="Vouchfx.Engine.Abstractions.StepOutcome"/>
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
/// <param name="Avro">
/// The optional Avro / schema-registry decoding spec.  <see langword="null"/> selects the
/// PLAIN-payload path (the match criteria run against the UTF-8 message value).  When
/// non-<see langword="null"/>, the consumed message is Avro-decoded to a
/// <c>GenericRecord</c>, converted to a JSON string, and the same <see cref="Match"/>
/// criteria run against that JSON string.  Additive trailing field (§13).
/// </param>
public sealed record MqExpectKafkaModel(
    string Target,
    string Topic,
    KafkaMatch Match,
    KafkaAvro? Avro = null) : IStepModel;

/// <summary>
/// The Avro / schema-registry decoding spec for an <c>mq-expect.kafka</c> step
/// (DSL §5, §13).  Present (non-<see langword="null"/> on the model) only when the author
/// declares an <c>avro</c> block; its presence switches the provider from the PLAIN
/// string-consumer path to the Avro <c>GenericRecord</c> path.
/// </summary>
/// <remarks>
/// <para>
/// For the expect side the deserializer fetches the writer schema from the registry by the
/// message's embedded schema id, so <see cref="Subject"/> and <see cref="Schema"/> are
/// optional (carried for symmetry / future reader-schema use).  The decoded
/// <c>GenericRecord</c> is converted to a JSON string and the existing
/// <see cref="MqExpectKafkaModel.Match"/> criteria run against it unchanged.
/// </para>
/// </remarks>
/// <param name="SchemaRegistryTarget">
/// Logical name of the kafka dependency whose schema registry this step consumes through.
/// The registry URL is staged by the orchestrator under <c>svc::&lt;target&gt;-sr</c> and
/// read at execution time via <c>VarKeys.Service(SchemaRegistryTarget + "-sr")</c>.
/// </param>
/// <param name="Subject">
/// Optional schema-registry subject (informational for the expect side; the writer schema
/// is fetched by the message's embedded schema id).
/// </param>
/// <param name="Schema">
/// Optional inline reader schema as avsc JSON (informational for the expect side).
/// </param>
public sealed record KafkaAvro(
    string SchemaRegistryTarget,
    string? Subject,
    string? Schema);

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
