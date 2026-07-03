// Platform.Steps.MqExpect.Rabbitmq — mq-expect.rabbitmq step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Platform.Sdk;

namespace Platform.Steps.MqExpect.Rabbitmq;

/// <summary>
/// Strongly-typed model for the <c>mq-expect.rabbitmq</c> step kind (DSL §5).
/// </summary>
/// <param name="Target">
/// Logical name of the rabbitmq dependency to consume from, as declared under
/// <c>environment.dependencies</c>.  Resolved to an AMQP URI by the orchestrator.
/// </param>
/// <param name="Queue">
/// The AMQP queue to consume messages from.
/// </param>
/// <param name="Match">
/// The assertion criteria a consumed message must satisfy.  At least one criterion
/// must be declared; an empty match is rejected by validation.
/// </param>
public sealed record MqExpectRabbitmqModel(
    string Target,
    string Queue,
    RabbitmqMatch Match) : IStepModel;

/// <summary>
/// The criteria a consumed RabbitMQ message must satisfy.
/// </summary>
/// <remarks>
/// All criteria are conjunctive (logical AND).  At least one must be non-null/non-empty.
/// </remarks>
/// <param name="PayloadContains">
/// Optional substring the UTF-8 message body must contain (ordinal).
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Headers">
/// Optional map of expected header names to their expected string values.
/// </param>
/// <param name="Json">
/// Optional map of JSONPath expressions to their expected string values.
/// </param>
public sealed record RabbitmqMatch(
    string? PayloadContains,
    IReadOnlyDictionary<string, string>? Headers,
    IReadOnlyDictionary<string, string>? Json);
