// Platform.Steps.DbAssert.Postgres — db-assert.postgres step model (DSL §5, §13.10).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Platform.Sdk;

namespace Platform.Steps.DbAssert.Postgres;

/// <summary>
/// Assertion expectations for a <c>db-assert.postgres</c> step.
/// </summary>
/// <remarks>
/// At least one of <see cref="RowCount"/> or <see cref="Row"/> must be specified;
/// the validator enforces this requirement.
/// </remarks>
/// <param name="RowCount">
/// The expected number of rows returned by the query, or <see langword="null"/>
/// when the caller does not assert on row count.
/// </param>
/// <param name="Row">
/// An optional map of column name to expected string value, asserted against
/// the first row of the result set.  <see langword="null"/> when the caller
/// does not assert on individual column values.
/// </param>
public sealed record PostgresExpectation(
    int? RowCount,
    IReadOnlyDictionary<string, string>? Row);

/// <summary>
/// Strongly-typed model for the <c>db-assert.postgres</c> step kind (DSL §5).
/// </summary>
/// <param name="Target">
/// Logical name of the postgres dependency to query, as declared under
/// <c>environment.dependencies</c>.  Resolved to a connection string by
/// the orchestrator at execution time.
/// </param>
/// <param name="Query">
/// The SQL query to execute against the target database.  May be a multi-line
/// string; no parameterisation is performed by the model itself.
/// </param>
/// <param name="Parameters">
/// An optional map of parameter names (without the leading <c>@</c>) to their
/// string values.  The executor binds these as <c>NpgsqlParameter</c> instances
/// to prevent SQL injection.
/// </param>
/// <param name="Expect">
/// The assertion block declaring the expected query outcome.  At least one of
/// <see cref="PostgresExpectation.RowCount"/> or <see cref="PostgresExpectation.Row"/>
/// must be specified.
/// </param>
public sealed record DbAssertPostgresModel(
    string Target,
    string Query,
    IReadOnlyDictionary<string, string>? Parameters,
    PostgresExpectation Expect) : IStepModel;
