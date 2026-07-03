// MqPublishNatsModel — strongly-typed binding model for the mq-publish.nats step kind.
//
// All fields mirror the DSL §5 declaration.  The model is a record (immutable by design)
// so that multiple steps in the same suite can be validated and compiled concurrently
// without shared-mutable-state races.
//
// No Avro in v1 (NATS has no built-in Schema Registry).
// No headers in v1 (simplification — focus on core JetStream publish).
using Platform.Sdk;

namespace Platform.Steps.MqPublish.Nats;

/// <summary>
/// Binding model for the <c>mq-publish.nats</c> step kind.
/// Carries the YAML-authored field values after the Bind pass;
/// the Validate and Emit passes read but do not mutate these fields.
/// </summary>
/// <param name="Target">
/// Logical name of the NATS dependency to publish to, as declared under
/// <c>environment.dependencies</c>.  Used as the look-up key for the NATS
/// connection URL staged in <c>Vars</c> (key = <c>conn::&lt;target&gt;</c>).
/// </param>
/// <param name="Subject">
/// The NATS subject to publish to.  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at step-execution time (§17).
/// </param>
/// <param name="Stream">
/// Optional JetStream stream name.  When <see langword="null"/> or absent, the
/// stream name is derived from <see cref="Subject"/> by uppercasing and replacing
/// non-alphanumeric characters with underscores (consecutive underscores collapsed).
/// </param>
/// <param name="Payload">
/// UTF-8 payload bytes (after encoding).  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at step-execution time (§17).
/// </param>
public sealed record MqPublishNatsModel(
    string Target,
    string Subject,
    string? Stream,
    string Payload) : IStepModel;
