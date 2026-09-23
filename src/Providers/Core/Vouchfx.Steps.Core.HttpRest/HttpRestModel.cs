// Vouchfx.Steps.Core.HttpRest — http.rest step model (DSL §5).
// Strongly-typed record; Dictionary<string,object> is explicitly prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.HttpRest;

/// <summary>
/// One <c>expect.json</c> entry: a JSONPath and what the response body must hold there.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Expected"/> and <see cref="Exists"/> is set on a well-formed
/// entry — the scalar form sets <see cref="Expected"/>, the <c>{ exists: … }</c> form sets
/// <see cref="Exists"/>. The schema enforces that on the engine's own path; the provider's
/// <c>Validate</c> refuses anything else for a caller that binds without validating.
/// </remarks>
/// <param name="Path">
/// The JSONPath expression (RFC 9535), exactly as written — it is never
/// <c>{placeholder}</c>-substituted or secret-resolved.
/// </param>
/// <param name="Expected">
/// The expected value, as a RAW template: compared as text with the selected node once any
/// <c>{placeholder}</c> / <c>${secret:source/path}</c> token has been resolved at
/// step-execution time. <see langword="null"/> for the <c>exists</c> form.
/// </param>
/// <param name="Exists">
/// <see langword="true"/> when the path must select at least one node,
/// <see langword="false"/> when it must select none; <see langword="null"/> for the scalar
/// (equality) form.
/// </param>
public sealed record HttpJsonAssertion(string Path, string? Expected, bool? Exists);

/// <summary>
/// Assertion expectations for an <c>http.rest</c> step response.
/// </summary>
/// <param name="Status">
/// The expected HTTP status code, or <see langword="null"/> when the caller
/// does not assert on the status code.
/// </param>
/// <param name="Json">
/// The <c>expect.json</c> entries in declaration order, or <see langword="null"/> when none
/// are declared. Evaluated against the response body only after the status check passes.
/// </param>
/// <param name="BodyContains">
/// The <c>expect.bodyContains</c> substring as a RAW template, or <see langword="null"/>
/// when not declared. Evaluated after the <see cref="Json"/> entries.
/// </param>
public sealed record HttpExpect(
    int? Status,
    IReadOnlyList<HttpJsonAssertion>? Json = null,
    string? BodyContains = null);

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
