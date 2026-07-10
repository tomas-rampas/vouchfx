// Vouchfx.Steps.MqPublish.Rabbitmq — mq-publish.rabbitmq step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.MqPublish.Rabbitmq;

/// <summary>
/// Strongly-typed model for the <c>mq-publish.rabbitmq</c> step kind (DSL §5).
/// </summary>
/// <param name="Target">
/// Logical name of the rabbitmq dependency to publish to, as declared under
/// <c>environment.dependencies</c>.  Resolved to an AMQP URI by the orchestrator.
/// </param>
/// <param name="Exchange">
/// Optional AMQP exchange name.  <see langword="null"/> / empty routes to the default exchange.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="RoutingKey">
/// The AMQP routing key.  For the default exchange this equals the queue name.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Payload">
/// The message payload sent as the AMQP message body (UTF-8 encoded).
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Headers">
/// Optional map of message-header names to their string values.  Each value may
/// contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at
/// runtime; header names are used verbatim.  <see langword="null"/> when the step
/// declares no headers.
/// </param>
public sealed record MqPublishRabbitmqModel(
    string Target,
    string? Exchange,
    string RoutingKey,
    string Payload,
    IReadOnlyDictionary<string, string>? Headers) : IStepModel;
