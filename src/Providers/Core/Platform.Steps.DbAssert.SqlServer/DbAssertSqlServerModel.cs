// Platform.Steps.DbAssert.SqlServer — db-assert.sqlserver step model (DSL §5, §13.10).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Platform.Sdk;

namespace Platform.Steps.DbAssert.SqlServer;

public sealed record SqlServerExpectation(
    int? RowCount,
    IReadOnlyDictionary<string, string>? Row);

public sealed record DbAssertSqlServerModel(
    string Target,
    string Query,
    IReadOnlyDictionary<string, string>? Parameters,
    SqlServerExpectation Expect) : IStepModel;
