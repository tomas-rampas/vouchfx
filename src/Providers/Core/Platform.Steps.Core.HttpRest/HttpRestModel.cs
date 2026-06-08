// Platform.Steps.Core.HttpRest — http.rest step model (DSL §5).
// Strongly-typed record; Dictionary<string,object> is explicitly prohibited (§13).
using System.Text.Json;
using Platform.Sdk;

namespace Platform.Steps.HttpRest;

/// <summary>
/// Assertion expectations for an <c>http.rest</c> step response.
/// </summary>
/// <param name="Status">
/// The expected HTTP status code, or <see langword="null"/> when the caller
/// does not assert on the status code.
/// </param>
public sealed record HttpExpect(int? Status);

/// <summary>
/// Strongly-typed model for the <c>http.rest</c> step kind (DSL §5.1).
/// </summary>
/// <param name="Target">
/// Logical name of the service to call, as declared under
/// <c>environment.services</c>.  Resolved to a real address by Aspire
/// service discovery at orchestration time.
/// </param>
/// <param name="Method">
/// The HTTP verb: <c>GET</c>, <c>POST</c>, <c>PUT</c>, <c>PATCH</c>,
/// <c>DELETE</c>, <c>HEAD</c>, or <c>OPTIONS</c>.
/// </param>
/// <param name="Path">
/// The request path, which may contain variable placeholders such as
/// <c>{basePath}/users</c>.
/// </param>
/// <param name="Headers">
/// An optional map of request header names to values.
/// </param>
/// <param name="Body">
/// An optional request body supplied inline; serialised to JSON at
/// execution time.
/// </param>
/// <param name="Expect">
/// An optional assertion block applied to the HTTP response.
/// </param>
public sealed record HttpRestModel(
    string Target,
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Headers,
    JsonElement? Body,
    HttpExpect? Expect) : IStepModel;
