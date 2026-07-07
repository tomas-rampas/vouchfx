// Platform.Engine.Authoring — StepNode AST record (S03-B-02).
//
// A normalised, fully-resolved representation of a single step produced by
// AstBuilder.  All raw strings from StepSpec have been parsed and defaulted;
// downstream stages (providers, the CSX compiler) consume StepNode, not
// StepSpec.

using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Authoring.Ast;

/// <summary>
/// Normalised AST node for a single test step, produced by
/// <see cref="AstBuilder.Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every field that is optional or defaulted in the YAML DSL (§4) has been
/// resolved to its final value.  Downstream stages (provider binders, the
/// CSX compiler, and the execution layer) depend only on this record and do
/// not need to handle null or raw-string fields.
/// </para>
/// <para>
/// <see cref="RawNode"/> is forwarded unchanged from
/// <see cref="Model.StepSpec.RawNode"/> so that each provider's
/// <c>IStepBinder&lt;T&gt;.Bind</c> can extract provider-specific fields
/// without loss of information or line-number context.
/// </para>
/// </remarks>
/// <param name="Id">
/// Unique step identifier within the scenario, used in reporting and failure
/// messages.  Sourced directly from the YAML <c>id</c> field.
/// </param>
/// <param name="Kind">
/// The resolved step kind composed of family and provider, e.g.
/// <c>Family="http", Provider="rest"</c>.  <see cref="AstBuilder"/> accepts
/// only the dotted <c>family.provider</c> form; bare family names are
/// rejected before this node is created.
/// </param>
/// <param name="CanonicalType">
/// The dotted string representation of the resolved kind,
/// i.e. <c>"&lt;family&gt;.&lt;provider&gt;"</c>.  Used in event stream
/// payloads for the <c>kind</c> field (§14).
/// </param>
/// <param name="RawNode">
/// The full YAML mapping node for this step.  Forwarded verbatim to the
/// provider binder.  <see cref="YamlNode.Start"/> carries 1-based line and
/// column information for diagnostics.
/// </param>
/// <param name="Capture">
/// Map of variable name to a typed extractor
/// (<see cref="Platform.Sdk.CaptureExpr"/>, carrying both the query language —
/// JSONPath or XPath — and the raw expression string).  Never
/// <see langword="null"/>; the empty dictionary is used when the YAML step
/// has no <c>capture</c> section.
/// </param>
/// <param name="VerifyMode">
/// Resolved assertion-evaluation strategy.  Defaults to
/// <see cref="Platform.Engine.Abstractions.VerifyMode.Immediate"/> when the
/// YAML step omits <c>verifyMode</c>.
/// </param>
/// <param name="Timeout">
/// Parsed total-time budget for the step, or <see langword="null"/> when
/// absent (the engine applies its own default).  For
/// <see cref="Platform.Engine.Abstractions.VerifyMode.Retry"/> steps this
/// bounds the polling window (§4).
/// </param>
/// <param name="ContinueOnFailure">
/// When <see langword="true"/>, a step failure is recorded but does not abort
/// subsequent steps.  Defaults to <see langword="false"/> when the YAML step
/// omits <c>continueOnFailure</c>.
/// </param>
public sealed record StepNode(
    string Id,
    StepKindId Kind,
    string CanonicalType,
    YamlMappingNode RawNode,
    IReadOnlyDictionary<string, CaptureExpr> Capture,
    VerifyMode VerifyMode,
    TimeSpan? Timeout,
    bool ContinueOnFailure);
