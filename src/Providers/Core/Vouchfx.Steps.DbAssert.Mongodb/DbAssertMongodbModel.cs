// Vouchfx.Steps.DbAssert.Mongodb — db-assert.mongodb step model (DSL §5, §13.10).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.DbAssert.Mongodb;

public sealed record MongoExpectation(
    long? Count,
    IReadOnlyDictionary<string, string>? Document);

public sealed record DbAssertMongodbModel(
    string Target,
    string Collection,
    string Filter,
    MongoExpectation Expect) : IStepModel;
