// Platform.Steps.Script.Csharp — script.csharp step provider (DSL §5, §13).
//
// Allows a test author to embed a block of raw C# that runs inside the compiled
// CSX submission with access to Vars.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own field (code).  The type
//     const discriminator is injected by SchemaComposer from Kind.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     is empty (no shared static class needed); StatementBlock is assembled with
//     a StringBuilder — the author body is NEVER placed inside a $$"""…""" hole.
using System.Text;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.Script.Csharp;

/// <summary>
/// Core provider for the <c>script.csharp</c> step kind (DSL §5, §13).
/// Lets a test author embed an inline C# block that runs inside the compiled
/// CSX submission with access to <c>Vars</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.
/// The engine's <c>SchemaComposer</c> assembles the unified schema by injecting
/// a <c>const</c>-keyed <c>if</c>/<c>then</c> discriminator derived from
/// <see cref="Kind"/> — the fragment text never repeats that discriminator (§13.6).
/// </para>
/// <para>
/// The <see cref="Emit"/> method assembles the <see cref="CsxFragment"/>
/// <see cref="CsxFragment.StatementBlock"/> with a <see cref="StringBuilder"/>,
/// splicing the author's C# body <em>verbatim</em> as a literal substring.  The
/// author body is <strong>never</strong> placed inside a <c>$$"""…"""</c>
/// interpolation hole, because the author body may itself contain braces,
/// string interpolations, raw-string fences, or any other C# syntax that would
/// corrupt or break out of such a hole (§13.3.1).
/// </para>
/// </remarks>
[StepProvider]
public sealed class ScriptCsharpProvider
    : IStepProvider,
      IStepBinder<ScriptCsharpModel>,
      IStepValidator<ScriptCsharpModel>,
      IStepCompiler<ScriptCsharpModel>
{
    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the engine-owned scaffolding in every emitted step block.
    /// Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Diagnostics",
            "Platform.Engine.Abstractions",
        };

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("script", "csharp");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<ScriptCsharpModel> ───────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>script.csharp</c>
    /// provider's own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it
    /// as an <c>if</c>/<c>then</c> clause (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["code"],
          "properties": {
            "code": {
              "description": "Inline C# code block executed inside the compiled CSX submission.  Has access to the shared Vars dictionary.",
              "type": "string"
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public ScriptCsharpModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
            return new ScriptCsharpModel(Code: string.Empty);

        var code = mapping.Children.TryGetValue(new YamlScalarNode("code"), out var codeNode)
                   && codeNode is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

        return new ScriptCsharpModel(Code: code);
    }

    // ── IStepValidator<ScriptCsharpModel> ────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(ScriptCsharpModel model, IProjectContext ctx)
    {
        if (string.IsNullOrWhiteSpace(model.Code))
        {
            return ValidationResult.Failure(
                "script.csharp: 'code' must not be empty or whitespace.");
        }

        return ValidationResult.Success;
    }

    // ── IStepCompiler<ScriptCsharpModel> ─────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Assembles the <see cref="CsxFragment.StatementBlock"/> with a
    /// <see cref="StringBuilder"/>, wrapping the author's body in engine-owned
    /// scaffolding that:
    /// <list type="bullet">
    ///   <item>starts a <c>Stopwatch</c> and initialises the outcome locals;</item>
    ///   <item>splices the author body verbatim inside a <c>try</c> block;</item>
    ///   <item>catches any <see cref="Exception"/> thrown by the author body and
    ///         records it as <see cref="Verdict.Fail"/>;</item>
    ///   <item>unconditionally stops the stopwatch in <c>finally</c>;</item>
    ///   <item>writes the <see cref="StepOutcome"/> to
    ///         <c>Vars[VarKeys.Outcome(safeId)]</c> — this write is outside the
    ///         try/finally and therefore always executes.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Security — brace-balance property.</strong>
    /// The author body is spliced verbatim inside <c>try { … }</c>.  The engine
    /// wrapper is brace-balanced: if the author body deliberately or accidentally
    /// introduces unbalanced braces (e.g. <c>} evil; {</c>), the assembled text
    /// will contain unbalanced braces and Roslyn will refuse to compile it
    /// (<c>ScriptCompilationException</c> / Inconclusive verdict).  That is a
    /// compile error — not a silent clobber of the outcome write or another step's
    /// state.  The engine's outcome write is positioned <em>after</em> the
    /// <c>finally</c> and therefore cannot be removed by any author-body brace
    /// trick that still leaves the text compilable.
    /// </para>
    /// <para>
    /// The author body is <strong>never</strong> placed inside a
    /// <c>$$"""…"""</c> interpolation hole.  Doing so would corrupt any author
    /// code that contains <c>{{</c>/<c>}}</c> sequences, raw-string fences, or
    /// string interpolations.  StringBuilder concatenation of literal substrings
    /// is the only safe approach (§13.3.1).
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — empty; no shared static class is needed.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — one brace-enclosed block; no <c>using var</c>.</item>
    ///   <item>The outcome key is the only value derived from non-author input; it is
    ///         emitted via <c>JsonSerializer.Serialize</c> so it is a safe C# string literal.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(ScriptCsharpModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // The outcome key is engine-derived only (never from author input).
        // JsonSerializer.Serialize wraps it in double-quotes and escapes any
        // special characters, producing a safe C# string literal.
        var outcomeKeyLiteral = JsonSerializer.Serialize(VarKeys.Outcome(safeId));

        // Build the StatementBlock with a StringBuilder.
        // The author body is appended VERBATIM — no escaping, no interpolation hole.
        // Every other part of the wrapper is a fixed string literal appended around it.
        //
        // All engine-introduced locals carry the safeId suffix so that two
        // script.csharp steps in the same suite never collide:
        //   __sw_<safeId>   — Stopwatch
        //   __v_<safeId>    — Verdict
        //   __obs_<safeId>  — observation string
        //   __ex_<safeId>   — caught exception (catch-clause parameter)
        var sb = new StringBuilder();

        sb.Append("{\n");
        sb.Append("    var __sw_").Append(safeId).Append(" = System.Diagnostics.Stopwatch.StartNew();\n");
        sb.Append("    Platform.Engine.Abstractions.Verdict __v_").Append(safeId)
          .Append(" = Platform.Engine.Abstractions.Verdict.Pass;\n");
        sb.Append("    string? __obs_").Append(safeId).Append(" = null;\n");
        sb.Append("    try\n");
        sb.Append("    {\n");
        sb.Append("        // ---- begin author code (spliced verbatim) ----\n");
        sb.Append(model.Code);
        sb.Append("\n        // ---- end author code ----\n");
        sb.Append("    }\n");
        sb.Append("    catch (System.Exception __ex_").Append(safeId).Append(")\n");
        sb.Append("    {\n");
        sb.Append("        __v_").Append(safeId)
          .Append(" = Platform.Engine.Abstractions.Verdict.Fail;\n");
        sb.Append("        __obs_").Append(safeId)
          .Append(" = __ex_").Append(safeId).Append(".Message;\n");
        sb.Append("    }\n");
        sb.Append("    finally\n");
        sb.Append("    {\n");
        sb.Append("        __sw_").Append(safeId).Append(".Stop();\n");
        sb.Append("    }\n");
        sb.Append("    Vars[").Append(outcomeKeyLiteral)
          .Append("] = new Platform.Engine.Abstractions.StepOutcome(__v_").Append(safeId)
          .Append(", __sw_").Append(safeId)
          .Append(".ElapsedMilliseconds, __obs_").Append(safeId).Append(");\n");
        sb.Append('}');

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: sb.ToString());
    }
}
