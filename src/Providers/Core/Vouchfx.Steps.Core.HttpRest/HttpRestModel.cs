// Vouchfx.Steps.Core.HttpRest — http.rest step model (DSL §5).
// Strongly-typed record; Dictionary<string,object> is explicitly prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.HttpRest;

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
/// An optional request body, stored as a raw template string.  A YAML scalar
/// body is kept as its literal string; a YAML mapping/sequence body is
/// serialised to a JSON string at <see cref="HttpRestProvider.Bind"/> time.
/// The template is emitted RAW (never pre-resolved): <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens are resolved at step-execution time
/// inside the emitted helper's guarded region (§17), exactly like the path and
/// header values.  <see langword="null"/> when no body is declared.
/// </param>
/// <param name="Expect">
/// An optional assertion block applied to the HTTP response.
/// </param>
public sealed record HttpRestModel(
    string Target,
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Headers,
    string? Body,
    HttpExpect? Expect) : IStepModel;
