// MqExpectRedisModel — strongly-typed binding model for the mq-expect.redis step kind.
//
// Semantics: Redis Streams (XRANGE).  No key or headers in v1 (match criteria are
// payload-only: substring + JSONPath) — mirrors mq-expect.nats's NatsMatch exactly.
using Vouchfx.Sdk;

namespace Vouchfx.Steps.MqExpect.Redis;

/// <summary>
/// Match criteria for the <c>mq-expect.redis</c> step kind.
/// At least one of <see cref="PayloadContains"/> or <see cref="Json"/> must be set;
/// validation rejects a model where both are null / empty (no effective criterion).
/// </summary>
/// <param name="PayloadContains">
/// Optional substring the UTF-8 message payload must contain (ordinal comparison).
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved
/// at step-execution time (§17).
/// </param>
/// <param name="Json">
/// Optional map of JSONPath expressions to their expected string values, evaluated
/// over the message payload parsed as JSON.  Keys are JSONPath expressions; values
/// are the expected string representations.  May contain
/// <c>${secret:source/path}</c> tokens in the values (§17).
/// </param>
public sealed record RedisMatch(
    string? PayloadContains,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Json);

/// <summary>
/// Binding model for the <c>mq-expect.redis</c> step kind.
/// </summary>
/// <param name="Target">
/// Logical name of the Redis dependency to consume from, as declared under
/// <c>environment.dependencies</c>.
/// </param>
/// <param name="Stream">
/// The Redis Stream key to scan via <c>XRANGE</c>.  May contain <c>{placeholder}</c>
/// tokens resolved at step-execution time.
/// </param>
/// <param name="Match">
/// Match criteria.  At least one effective criterion must be declared.
/// </param>
public sealed record MqExpectRedisModel(
    string Target,
    string Stream,
    RedisMatch Match) : IStepModel;
