// MqPublishRedisModel — strongly-typed binding model for the mq-publish.redis step kind.
//
// All fields mirror the DSL §5 declaration.  The model is a record (immutable by design)
// so that multiple steps in the same suite can be validated and compiled concurrently
// without shared-mutable-state races.
//
// Semantics: Redis Streams (XADD).  Unlike mq-publish.nats (subject + optional derived
// JetStream stream name), a Redis Stream key IS the addressing unit — there is no separate
// "subject" concept and therefore no stream-name derivation logic; the author names the
// stream directly.
using Platform.Sdk;

namespace Platform.Steps.MqPublish.Redis;

/// <summary>
/// Binding model for the <c>mq-publish.redis</c> step kind.
/// Carries the YAML-authored field values after the Bind pass;
/// the Validate and Emit passes read but do not mutate these fields.
/// </summary>
/// <param name="Target">
/// Logical name of the <c>redis</c> dependency to publish to, as declared under
/// <c>environment.dependencies</c>.  Used as the look-up key for the Redis connection
/// string staged in <c>Vars</c> (key = <c>conn::&lt;target&gt;</c>).
/// </param>
/// <param name="Stream">
/// The Redis Stream key to <c>XADD</c> to.  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at step-execution time (§17).
/// </param>
/// <param name="Payload">
/// The message payload, written as the UTF-8 string value of the canonical
/// <c>payload</c> stream field (see <c>MqPublishRedis_Helpers</c>).  May contain
/// <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at
/// step-execution time (§17).
/// </param>
public sealed record MqPublishRedisModel(
    string Target,
    string Stream,
    string Payload) : IStepModel;
