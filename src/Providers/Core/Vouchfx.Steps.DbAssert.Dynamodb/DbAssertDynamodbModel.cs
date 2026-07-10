// Vouchfx.Steps.DbAssert.Dynamodb — db-assert.dynamodb step model (DSL §5, §13.10).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.DbAssert.Dynamodb;

/// <summary>
/// The assertion block for a <c>db-assert.dynamodb</c> step.
/// </summary>
/// <param name="Exists">
/// Whether the item is expected to exist.  Defaults to <see langword="true"/> when the
/// author omits the field (bound at <see cref="DbAssertDynamodbProvider.Bind"/> time).
/// </param>
/// <param name="Item">
/// Optional flat map of attribute name to expected string value, compared against the
/// GetItem result's top-level attributes (stringified: <c>S</c> as-is, <c>N</c> the raw
/// number string, <c>BOOL</c> as <c>"true"</c>/<c>"false"</c>; nested/list/map attributes
/// are out of v1 scope — see the provider's file header).  <see langword="null"/> when no
/// item-field assertions are declared.
/// </param>
public sealed record DynamoExpectation(
    bool? Exists,
    IReadOnlyDictionary<string, string>? Item);

/// <summary>
/// Strongly-typed model for the <c>db-assert.dynamodb</c> step kind.
/// </summary>
/// <param name="Target">
/// Logical name of the <c>dynamodb</c> dependency to query, as declared under
/// <c>environment.dependencies</c>.
/// </param>
/// <param name="Table">
/// Name of the DynamoDB table to query.  May contain <c>{placeholder}</c> tokens.
/// </param>
/// <param name="Key">
/// A JSON object template naming the primary key (partition key, optionally plus a sort
/// key) for a <c>GetItem</c> call, e.g. <c>{"orderId":"{orderId}"}</c>.  Each top-level
/// JSON value becomes a DynamoDB attribute value at execution time: a JSON string becomes
/// an <c>S</c> attribute, a JSON number becomes an <c>N</c> attribute (stored as DynamoDB's
/// own decimal-string form), and a JSON boolean becomes a <c>BOOL</c> attribute.  May
/// contain <c>{placeholder}</c> tokens inside JSON string values; see the provider's file
/// header for the JSON-escape injection guard applied to resolved values.
/// </param>
/// <param name="Expect">
/// The assertion block declaring the expected GetItem outcome.
/// </param>
public sealed record DbAssertDynamodbModel(
    string Target,
    string Table,
    string Key,
    DynamoExpectation Expect) : IStepModel;
