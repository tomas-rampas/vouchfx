// Vouchfx.Sdk — provider-authoring contract surface (§13).
// This file declares CsxFragment and its composition rules (§13.3.1).
namespace Vouchfx.Sdk;

/// <summary>
/// Represents the three-part CSX contribution emitted by a provider compiler
/// for a single step instance.  The engine merges contributions from all steps
/// before handing the assembled script to Roslyn (§5, §13.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Composition rules (§13.3.1) — these rules prevent identifier and
/// class-name collisions across providers:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="RequiredUsings"/> must contain bare namespace strings only —
///       never inline <c>using</c> directives.  The engine deduplicates and
///       emits the <c>using</c> lines at the top of the script.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="RequiredHelpers"/> entries are the <em>full source</em> of
///       nested <c>static</c> classes whose declared class names must be
///       prefixed with the provider identifier followed by an underscore, e.g.
///       <c>static class DbAssertPostgres_Helpers { … }</c>.  The engine
///       deduplicates entries by declared class name and injects the resulting
///       unique definitions before the main script body.  A provider
///       <strong>must</strong> emit byte-identical source for a given helper
///       class across all of its step instances within one suite; two steps of
///       the same kind must not produce two differing definitions of the same
///       class.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="StatementBlock"/> is exactly one brace-enclosed
///       <c>{ … }</c> block.  <c>using var</c> is illegal inside a Roslyn
///       script body (parse error); use plain <c>var</c> with an explicit
///       <c>.Dispose()</c> in a <c>finally</c> block.
///     </description>
///   </item>
///   <item>
///     <description>
///       Emit statement bodies as C# 11 double-dollar raw strings
///       (<c>$$"""…"""</c>): with two <c>$</c>, a single <c>{</c>/<c>}</c>
///       is a <em>literal brace</em> in the emitted CSX (so the step's own
///       <c>{ … }</c> block passes through verbatim) and <c>{{expr}}</c> is
///       the interpolation hole the emitter fills.  The single-dollar form
///       inverts these semantics and will produce incorrect output.
///     </description>
///   </item>
///   <item>
///     <description>
///       Sanitise step identifiers before splicing them into variable names
///       using <see cref="SanitiseId"/>.  Emitted variable names must not
///       contain hyphens.
///     </description>
///   </item>
///   <item>
///     <description>
///       Cross-step state must pass exclusively through the <c>Vars</c>
///       global; never assume that variables declared by another provider are
///       in scope.
///     </description>
///   </item>
/// </list>
/// </remarks>
/// <param name="RequiredUsings">
/// Bare namespace strings that the engine will emit as top-level
/// <c>using</c> directives before the script body.  Must not contain
/// the <c>using</c> keyword or a trailing semicolon.
/// </param>
/// <param name="RequiredHelpers">
/// Full source of nested <c>static</c> helper class definitions contributed
/// by this provider.  Each entry must contain exactly one class whose
/// declared name is prefixed with the provider identifier followed by an
/// underscore, e.g. <c>static class NoopEcho_Helpers { … }</c>.  The engine
/// deduplicates by declared class name when assembling the script; a provider
/// must therefore emit byte-identical source for a given helper across all of
/// its step instances (two steps of the same kind must not produce two
/// differing definitions of the same class).
/// </param>
/// <param name="StatementBlock">
/// Exactly one brace-enclosed C# statement block for this step.
/// Must begin with <c>{</c> and end with <c>}</c>.
/// Must not contain inline <c>using</c> directives or <c>using var</c>.
/// </param>
public sealed record CsxFragment(
    IReadOnlyList<string> RequiredUsings,
    IReadOnlyList<string> RequiredHelpers,
    string StatementBlock)
{
    /// <summary>
    /// Converts a step identifier that may contain hyphens into a valid
    /// C# identifier fragment by replacing every hyphen (<c>-</c>) with an
    /// underscore (<c>_</c>).
    /// </summary>
    /// <remarks>
    /// All emitted variable names that derive from a step <c>id</c> field
    /// must pass through this method before splicing into a
    /// <see cref="StatementBlock"/>.  Hyphens are legal in YAML step ids
    /// but illegal in C# identifiers.
    /// </remarks>
    /// <param name="stepId">
    /// The raw step identifier from the YAML source, which may contain
    /// hyphens.
    /// </param>
    /// <returns>
    /// The identifier with every hyphen replaced by an underscore.
    /// </returns>
    public static string SanitiseId(string stepId) => stepId.Replace('-', '_');
}
