// Platform.Steps.MqPublish.AzureServiceBus — step model (§13.3.1).
//
// Strongly-typed record for the mq-publish.azureservicebus step (DSL §5).
// Models are ALWAYS strongly-typed records — never Dictionary<string,object> (§13 hard invariant).
using System.Collections.Generic;
using Platform.Sdk;

namespace Platform.Steps.MqPublish.AzureServiceBus;

/// <summary>
/// Strongly-typed model for the <c>mq-publish.azureservicebus</c> step (DSL §5).
/// </summary>
/// <param name="Target">
/// Logical name of the <c>azureservicebus</c> dependency (as declared in
/// <c>environment.dependencies</c>).
/// </param>
/// <param name="Queue">
/// The target queue name.  Exactly one of <see cref="Queue"/> or <see cref="Topic"/>
/// must be set.  May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Topic">
/// The target topic name.  Exactly one of <see cref="Queue"/> or <see cref="Topic"/>
/// must be set.  May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Payload">
/// The message body sent as UTF-8 bytes.
/// May contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Properties">
/// Optional application properties attached to the message (key=value string pairs).
/// Values may contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens.
/// </param>
public sealed record MqPublishAzureServiceBusModel(
    string Target,
    string? Queue,
    string? Topic,
    string Payload,
    IReadOnlyDictionary<string, string>? Properties
) : IStepModel;
