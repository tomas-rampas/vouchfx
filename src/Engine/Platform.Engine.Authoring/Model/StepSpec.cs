// Platform.Engine.Authoring — StepSpec (S03-B-01).
//
// Strongly-typed record for a single step entry in the `steps` sequence of a
// .e2e.yaml file (docs/02 §4).

using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Authoring.Model;

/// <summary>
/// Parsed representation of a single step in the <c>steps</c> sequence (§4).
/// </summary>
/// <remarks>
/// <para>
/// The common fields defined in §4 are captured into typed properties.
/// All provider-specific fields are retained verbatim in <see cref="RawNode"/>
/// so that each provider's <c>IStepBinder&lt;T&gt;.Bind</c> implementation can
/// extract them without loss of information or line-number context.
/// </para>
/// <para>
/// <see cref="RawNode"/> is the full step mapping node, including the common
/// fields; callers should treat it as the authoritative source for provider
/// fields and use the typed properties only for engine-level concerns (routing,
/// reporting, retry logic).
/// </para>
/// </remarks>
/// <param name="Id">
/// Unique step identifier within the file, used in reporting and failure messages
/// (§4, Table 4.1).  Required.
/// </param>
/// <param name="Type">
/// Step type in dotted <c>family.provider</c> form, e.g. <c>http.rest</c> or
/// <c>db-assert.postgres</c>.  The bare family name is also accepted for families
/// with a single registered provider.  Required (§4, §5.7).
/// </param>
/// <param name="Description">
/// Optional short human-readable explanation shown in test output (§4).
/// </param>
/// <param name="Capture">
/// Optional map of variable name to extractor expression (JSONPath / XPath).
/// After the step completes, each expression is evaluated against the result
/// and the value is written into the shared context (§4, §6.1).
/// </param>
/// <param name="VerifyMode">
/// Raw token: <c>IMMEDIATE</c> (default) or <c>RETRY</c>.  <see langword="null"/>
/// when absent (engine defaults to <c>IMMEDIATE</c>).  See §4, §7.
/// </param>
/// <param name="Timeout">
/// Raw duration string, e.g. <c>30s</c>, or <see langword="null"/> when absent.
/// For a <c>RETRY</c> step this bounds the polling window.  See §4.
/// </param>
/// <param name="ContinueOnFailure">
/// When <see langword="true"/>, a failed assertion is recorded but does not
/// abort subsequent steps.  <see langword="null"/> when absent (engine defaults
/// to <see langword="false"/>).  See §4.
/// </param>
/// <param name="RawNode">
/// The full YAML mapping node for this step.  Provider binders receive this node
/// to extract their own fields.  <see cref="YamlNode.Start"/> carries line/column
/// information for diagnostic messages.
/// </param>
public sealed record StepSpec(
    string Id,
    string Type,
    string? Description,
    IReadOnlyDictionary<string, string>? Capture,
    string? VerifyMode,
    string? Timeout,
    bool? ContinueOnFailure,
    YamlMappingNode RawNode);
