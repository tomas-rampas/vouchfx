// Vouchfx.Steps.DbAssert.Mysql — db-assert.mysql step model (DSL §5, §13.10).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.DbAssert.Mysql;

public sealed record MysqlExpectation(
    int? RowCount,
    IReadOnlyDictionary<string, string>? Row);

public sealed record DbAssertMysqlModel(
    string Target,
    string Query,
    IReadOnlyDictionary<string, string>? Parameters,
    MysqlExpectation Expect) : IStepModel;
