// Vouchfx.Sdk — capture-expression value types (S07-B-01a).
// These types generalise the `capture:` model (DSL §6.1) so that a captured
// value may be extracted with either a JSONPath or an XPath expression.  They
// live in Vouchfx.Sdk because the same pair is consumed by the authoring AST
// (StepNode.Capture), the parser model (StepSpec.Capture), and the provider-facing
// ICompileContext (ICompileContext.CaptureExprs) — placing them on the shared
// SDK surface avoids a new inter-project dependency.
namespace Vouchfx.Sdk;

/// <summary>
/// Identifies the query language used by a single <c>capture:</c> expression to
/// extract a value from a step's result (DSL §6.1).
/// </summary>
/// <remarks>
/// <para>
/// The format is fixed at authoring time, not inferred at execution time: the
/// author either supplies a bare scalar (which defaults to
/// <see cref="JsonPath"/> for backward compatibility) or an explicit single-key
/// mapping (<c>{ jsonpath: … }</c> or <c>{ xpath: … }</c>).  The engine never
/// guesses the format from the shape of the expression string.
/// </para>
/// <para>
/// Evaluation of an <see cref="XPath"/> expression against a step result is the
/// responsibility of the consuming provider (e.g. <c>http.rest</c>) and is
/// introduced as a separate task; this enumeration only carries the authored
/// intent through the compilation pipeline.
/// </para>
/// </remarks>
public enum CaptureFormat
{
    /// <summary>
    /// The expression is a JSONPath query (JsonPath.Net, §5.7), evaluated against
    /// the JSON body of the step result.  This is the default for the bare-scalar
    /// authoring form (<c>name: "$.id"</c>), preserving pre-S07 behaviour.
    /// </summary>
    JsonPath,

    /// <summary>
    /// The expression is an XPath query, evaluated against the XML body of the
    /// step result.  Selected only via the explicit <c>{ xpath: … }</c> authoring
    /// form; never inferred.
    /// </summary>
    XPath,
}

/// <summary>
/// A single authored <c>capture:</c> extractor: the query language
/// (<see cref="Format"/>) paired with the raw extractor string
/// (<see cref="Expression"/>) the engine evaluates against a step's result
/// (DSL §6.1).
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="CaptureExpr"/> corresponds to one entry in a step's
/// <c>capture:</c> block; the variable name is the key in the surrounding
/// dictionary (e.g. the AST step node's <c>Capture</c> map) and is therefore
/// not repeated here.
/// </para>
/// <para>
/// The expression text is retained verbatim (never normalised or re-quoted) so
/// that the reproducibility envelope hashes the exact authored reference and
/// reporting can surface the literal path the author wrote (§14, §17).
/// </para>
/// </remarks>
/// <param name="Format">
/// The query language used to interpret <paramref name="Expression"/>.  Defaults
/// to <see cref="CaptureFormat.JsonPath"/> for the bare-scalar authoring form.
/// </param>
/// <param name="Expression">
/// The raw extractor string exactly as authored, e.g. <c>"$.id"</c> for
/// <see cref="CaptureFormat.JsonPath"/> or <c>"//order/id"</c> for
/// <see cref="CaptureFormat.XPath"/>.  Never <see langword="null"/>.
/// </param>
public sealed record CaptureExpr(CaptureFormat Format, string Expression);
