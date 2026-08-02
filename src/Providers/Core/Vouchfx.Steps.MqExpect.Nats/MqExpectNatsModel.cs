// MqExpectNatsModel — strongly-typed binding model for the mq-expect.nats step kind.
//
// No Avro in v1 (NATS has no built-in Schema Registry).
// No key or headers in v1 (match criteria are payload-only: substring + JSONPath).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.MqExpect.Nats;

/// <summary>
/// Match criteria for the <c>mq-expect.nats</c> step kind.
/// At least one of <see cref="PayloadContains"/> or <see cref="Json"/> must be set;
/// validation rejects a model where both are null / empty (no effective criterion).
/// </summary>
/// <param name="PayloadContains">
/// Optional substring the UTF-8 message payload must contain (ordinal comparison).
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved
/// at step-execution time (§17).
/// </param>
/// <param name="Json">
/// Optional map of JSONPath expressions to their expected values (read as text; a bare numeric or boolean scalar binds as its literal text), evaluated
/// over the message payload parsed as JSON.  Keys are JSONPath expressions; values
/// are the expected string representations.  May contain
/// <c>${secret:source/path}</c> tokens in the values (§17).
/// </param>
public sealed record NatsMatch(
    string? PayloadContains,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Json);

/// <summary>
/// Binding model for the <c>mq-expect.nats</c> step kind.
/// </summary>
/// <param name="Target">
/// Logical name of the NATS dependency to consume from, as declared under
/// <c>environment.dependencies</c>.
/// </param>
/// <param name="Subject">
/// The NATS subject to filter messages on.  May contain <c>{placeholder}</c>
/// tokens resolved at step-execution time.
/// </param>
/// <param name="Stream">
/// Optional JetStream stream name.  When absent, derived from <see cref="Subject"/>
/// (same rule as publish: uppercase + non-alphanumeric → underscore).
/// </param>
/// <param name="Match">
/// Match criteria.  At least one effective criterion must be declared.
/// </param>
public sealed record MqExpectNatsModel(
    string Target,
    string Subject,
    string? Stream,
    NatsMatch Match) : IStepModel;
