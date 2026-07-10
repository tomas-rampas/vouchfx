// Vouchfx.Steps.MqExpect.AzureServiceBus — step model (§13.3.1).
//
// Strongly-typed record for the mq-expect.azureservicebus step (DSL §5).
// Models are ALWAYS strongly-typed records — never Dictionary<string,object> (§13 hard invariant).
using System.Collections.Generic;
using Vouchfx.Sdk;

namespace Vouchfx.Steps.MqExpect.AzureServiceBus;

/// <summary>
/// Strongly-typed model for the <c>mq-expect.azureservicebus</c> step (DSL §5).
/// </summary>
/// <param name="Target">
/// Logical name of the <c>azureservicebus</c> dependency (as declared in
/// <c>environment.dependencies</c>).
/// </param>
/// <param name="Queue">
/// The source queue to peek.  Exactly one of <see cref="Queue"/> or
/// (<see cref="Topic"/> + <see cref="Subscription"/>) must be set.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Topic">
/// The source topic.  Requires <see cref="Subscription"/> to also be set.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Subscription">
/// The subscription on the <see cref="Topic"/> to peek.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="ExpectPayloadContains">
/// Optional substring the message body must contain for a match.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="ExpectProperties">
/// Optional application-property key=value pairs all of which must be present on
/// the matched message.  Values may contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens.
/// </param>
public sealed record MqExpectAzureServiceBusModel(
    string Target,
    string? Queue,
    string? Topic,
    string? Subscription,
    string? ExpectPayloadContains,
    IReadOnlyDictionary<string, string>? ExpectProperties
) : IStepModel;
