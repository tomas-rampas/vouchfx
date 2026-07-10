// Vouchfx.Steps.CacheAssert.Elasticsearch — model types (DSL §5, §13).
//
// Strongly-typed records produced by IStepBinder<T>.Bind and consumed by
// IStepValidator<T>.Validate and IStepCompiler<T>.Emit.  Models are NEVER
// Dictionary<string,object> — §13 hard invariant.
using Vouchfx.Sdk;

namespace Vouchfx.Steps.CacheAssert.Elasticsearch;

/// <summary>
/// Declares which top-level <c>_source</c> field to check and what value to expect on
/// the first hit returned by the Elasticsearch query.
/// </summary>
/// <param name="Field">
/// Top-level field name in the hit's <c>_source</c>.
/// Dot-notation is rejected at validation time (§13 — only flat field names supported).
/// </param>
/// <param name="Value">
/// Expected string value.  May contain <c>{placeholder}</c> tokens resolved at execution
/// time via <c>Secret_Helpers.ResolveTemplate</c> — never baked into the emitted IL (§17).
/// </param>
public sealed record EsFieldAssertion(string Field, string Value);

/// <summary>
/// Defines the expected result-set characteristics of an Elasticsearch query.
/// </summary>
/// <param name="Count">
/// Exact number of hits expected.  When set, overrides <paramref name="MinCount"/>.
/// </param>
/// <param name="MinCount">
/// Minimum number of hits expected (default 1).  Ignored when <paramref name="Count"/> is set.
/// </param>
/// <param name="Fields">
/// Optional field assertions evaluated against the first hit's <c>_source</c>.
/// Each entry checks that the named field equals the expected value.
/// </param>
public sealed record EsExpectation(
    int? Count,
    int MinCount,
    IReadOnlyList<EsFieldAssertion>? Fields);

/// <summary>
/// Strongly-typed model for the <c>cache-assert.elasticsearch</c> step kind.
/// Produced by <see cref="CacheAssertElasticsearchProvider"/> binding.
/// </summary>
/// <param name="Target">
/// Logical name of the <c>elasticsearch</c> dependency as declared under
/// <c>environment.dependencies</c>.  Drives the connection-string key
/// <c>VarKeys.Connection(Target)</c>.
/// </param>
/// <param name="Index">
/// Elasticsearch index to query.
/// </param>
/// <param name="Query">
/// Full Elasticsearch Query DSL JSON body (the entire request body, e.g.
/// <c>{"query":{"match":{"status":"active"}}}</c>).  When absent the helper
/// uses a <c>match_all</c> query.  May contain <c>{placeholder}</c> tokens
/// resolved at execution time (§17 — never baked into IL).
/// </param>
/// <param name="Expect">
/// Expected result-set characteristics (count and optional field assertions).
/// </param>
public sealed record CacheAssertElasticsearchModel(
    string Target,
    string Index,
    string? Query,
    EsExpectation Expect) : IStepModel;
