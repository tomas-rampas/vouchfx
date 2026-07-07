// Platform.Steps.CacheAssert.Redis — cache-assert.redis step model (DSL §5, §13).
//
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
//
// cache-assert began as a single-provider family (redis was its first Core provider).
// Steps always name the dotted form (`cache-assert.redis`) — bare family names are not
// part of the language.
using Platform.Sdk;

namespace Platform.Steps.CacheAssert.Redis;

/// <summary>
/// The Redis read operation a <c>cache-assert.redis</c> step performs against the
/// declared cache dependency.
/// </summary>
/// <remarks>
/// Each operation pairs with a specific <see cref="RedisExpectation"/> member (enforced by
/// <see cref="CacheAssertRedisProvider.Validate"/>):
/// <list type="bullet">
///   <item><description><see cref="Get"/> / <see cref="HGet"/> → <see cref="RedisExpectation.Value"/> (string equality).</description></item>
///   <item><description><see cref="Exists"/> → <see cref="RedisExpectation.Exists"/> (key presence).</description></item>
///   <item><description><see cref="Ttl"/> → <see cref="RedisExpectation.Exists"/> (has-a-positive-TTL presence check — see the TTL design note on <see cref="CacheAssertRedisProvider"/>).</description></item>
///   <item><description><see cref="HLen"/> / <see cref="LLen"/> / <see cref="SCard"/> → <see cref="RedisExpectation.Length"/> (long equality).</description></item>
/// </list>
/// </remarks>
public enum RedisOp
{
    /// <summary><c>GET key</c> — string value equality against <see cref="RedisExpectation.Value"/>.</summary>
    Get,

    /// <summary><c>EXISTS key</c> — key presence against <see cref="RedisExpectation.Exists"/>.</summary>
    Exists,

    /// <summary><c>TTL key</c> — asserts the key has (or has not) a positive time-to-live, via <see cref="RedisExpectation.Exists"/>.</summary>
    Ttl,

    /// <summary><c>HGET key field</c> — hash-field value equality against <see cref="RedisExpectation.Value"/>.</summary>
    HGet,

    /// <summary><c>HLEN key</c> — hash length equality against <see cref="RedisExpectation.Length"/>.</summary>
    HLen,

    /// <summary><c>LLEN key</c> — list length equality against <see cref="RedisExpectation.Length"/>.</summary>
    LLen,

    /// <summary><c>SCARD key</c> — set cardinality equality against <see cref="RedisExpectation.Length"/>.</summary>
    SCard,
}

/// <summary>
/// The expectation block for a <c>cache-assert.redis</c> step.  Exactly one member is
/// meaningful per <see cref="RedisOp"/>; <see cref="CacheAssertRedisProvider.Validate"/>
/// rejects a model whose expectation does not match its operation.
/// </summary>
/// <param name="Value">
/// Expected string value for <see cref="RedisOp.Get"/> / <see cref="RedisOp.HGet"/>.
/// May contain <c>{placeholder}</c> tokens resolved at step-execution time.
/// </param>
/// <param name="Exists">
/// Expected key presence for <see cref="RedisOp.Exists"/>, or expected
/// has-a-positive-TTL presence for <see cref="RedisOp.Ttl"/>.
/// </param>
/// <param name="Length">
/// Expected length / cardinality for <see cref="RedisOp.HLen"/> /
/// <see cref="RedisOp.LLen"/> / <see cref="RedisOp.SCard"/>.
/// </param>
public sealed record RedisExpectation(
    string? Value,
    bool? Exists,
    long? Length);

/// <summary>
/// Strongly-typed model for the <c>cache-assert.redis</c> step kind (DSL §5).
/// </summary>
/// <param name="Target">
/// Logical name of the <c>redis</c> dependency declared under
/// <c>environment.dependencies</c>.  The orchestration layer stages its connection
/// string at <c>conn::&lt;Target&gt;</c> via <see cref="VarKeys.Connection"/>.
/// </param>
/// <param name="Key">
/// The Redis key to inspect.  May contain <c>{placeholder}</c> tokens resolved at
/// step-execution time.
/// </param>
/// <param name="Operation">
/// The Redis read operation to perform.
/// </param>
/// <param name="Field">
/// The hash field name for <see cref="RedisOp.HGet"/> (required for that op only).
/// May contain <c>{placeholder}</c> tokens.  <see langword="null"/> for all other ops.
/// </param>
/// <param name="Expect">
/// The expectation block (value / exists / length).
/// </param>
public sealed record CacheAssertRedisModel(
    string Target,
    string Key,
    RedisOp Operation,
    string? Field,
    RedisExpectation Expect) : IStepModel;
