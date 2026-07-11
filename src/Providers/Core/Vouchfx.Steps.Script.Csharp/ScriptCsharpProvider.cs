// Vouchfx.Steps.Script.Csharp — script.csharp step provider (DSL §5, §13).
//
// Allows a test author to embed a block of raw C# — inline via `code`, or
// referenced from an external `.csx` file via `file` — that runs inside the
// compiled CSX submission with access to Vars. Exactly one of `code`/`file` is
// required; `file` is read once at compile time and spliced verbatim, exactly
// like `code`.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (code, file).
//     The type const discriminator is injected by SchemaComposer from Kind.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     is empty (no shared static class needed); StatementBlock is assembled with
//     a StringBuilder — the author body is NEVER placed inside a $$"""…""" hole.
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.Script.Csharp;

/// <summary>
/// Core provider for the <c>script.csharp</c> step kind (DSL §5, §13).
/// Lets a test author embed a C# block — inline via <c>code</c>, or referenced
/// from an external <c>.csx</c> file via <c>file</c> — that runs inside the
/// compiled CSX submission with access to <c>Vars</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.
/// The engine's <c>SchemaComposer</c> assembles the unified schema by injecting
/// a <c>const</c>-keyed <c>if</c>/<c>then</c> discriminator derived from
/// <see cref="Kind"/> — the fragment text never repeats that discriminator (§13.6).
/// </para>
/// <para>
/// <strong><c>code</c> vs. <c>file</c>.</strong> Exactly one is required
/// (enforced by <see cref="Validate"/>, not the JSON Schema's <c>oneOf</c>
/// alone — the model-level check gives a clear step-scoped
/// <see cref="ValidationResult.Failure(string)"/> message instead of a generic
/// schema-validation error). <c>file</c> is resolved relative to
/// <see cref="IProjectContext.SuiteDirectory"/> / <see cref="ICompileContext.SuiteDirectory"/>
/// (the scenario's own directory) and its content is read once, at compile
/// time, then spliced exactly like <c>code</c> — it is not re-read per
/// execution, and neither field participates in <c>{placeholder}</c> or
/// <c>${secret:…}</c> substitution (both are compile-time-only text, resolved
/// before any <c>Vars</c> exist).
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
/// <para>
/// <strong>Trust boundary (§13 — no sandbox).</strong> The author body runs in
/// the same compiled submission as every other step and has full ambient access
/// to the shared <c>Vars</c> dictionary.  This is an intentional escape-hatch
/// property of <c>script.csharp</c>: the author is trusted (it is their own test
/// code), so the body may read any staged value — <em>including connection
/// strings under <c>conn::…</c> that carry credentials</em> — and may write any
/// key.  The engine wraps the author body in an <c>async</c> local function
/// (<c>__body_&lt;safeId&gt;</c>) so that a <c>return;</c> statement in the
/// author code returns from the local function only, not from the entire Roslyn
/// submission delegate — the engine's own outcome write and all downstream steps
/// execute normally.  A brace-injection body that closes the local function early
/// produces orphaned syntax and fails to compile (→ Inconclusive), which is still
/// caught and does not silently clobber engine state.  Reserving the engine
/// reserved-key namespaces (<c>__outcome::</c>, <c>conn::</c>, <c>svc::</c>)
/// against author writes, and redacting credentials (§17 <c>SecretString</c>),
/// are post-MVP hardening items.
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
            "System.Threading.Tasks",
            "Vouchfx.Engine.Abstractions",
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
          "oneOf": [
            { "required": ["code"] },
            { "required": ["file"] }
          ],
          "properties": {
            "code": {
              "description": "Inline C# code block executed inside the compiled CSX submission.  Has access to the shared Vars dictionary.  Mutually exclusive with 'file'.",
              "type": "string"
            },
            "file": {
              "description": "Path to an external .csx file, resolved relative to the .e2e.yaml file's directory.  Read once at compile time and spliced verbatim, exactly like 'code'.  Mutually exclusive with 'code'.",
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
            return new ScriptCsharpModel(Code: null, File: null);

        var code = mapping.Children.TryGetValue(new YamlScalarNode("code"), out var codeNode)
                   && codeNode is YamlScalarNode codeScalar
            ? codeScalar.Value
            : null;

        var file = mapping.Children.TryGetValue(new YamlScalarNode("file"), out var fileNode)
                   && fileNode is YamlScalarNode fileScalar
            ? fileScalar.Value
            : null;

        return new ScriptCsharpModel(Code: code, File: file);
    }

    // ── IStepValidator<ScriptCsharpModel> ────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(ScriptCsharpModel model, IProjectContext ctx)
    {
        var hasCode = !string.IsNullOrWhiteSpace(model.Code);
        var hasFile = !string.IsNullOrWhiteSpace(model.File);

        if (!hasCode && !hasFile)
        {
            return ValidationResult.Failure(
                "script.csharp: exactly one of 'code' or 'file' must be set.");
        }

        if (hasCode && hasFile)
        {
            return ValidationResult.Failure(
                "script.csharp: 'code' and 'file' are mutually exclusive.");
        }

        if (hasFile)
        {
            var resolvedPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(ctx.SuiteDirectory, model.File!));

            if (!System.IO.File.Exists(resolvedPath))
            {
                return ValidationResult.Failure(
                    $"script.csharp: file '{model.File}' not found (resolved to '{resolvedPath}').");
            }
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
    ///   <item>declares an <c>async</c> local function <c>__body_&lt;safeId&gt;()</c>
    ///         and splices the author body verbatim inside it;</item>
    ///   <item>awaits the local function inside a <c>try</c> block;</item>
    ///   <item>catches any <see cref="Exception"/> thrown by the local function and
    ///         records it as <see cref="Verdict.Fail"/>;</item>
    ///   <item>unconditionally stops the stopwatch in <c>finally</c>;</item>
    ///   <item>writes the <see cref="StepOutcome"/> to
    ///         <c>Vars[VarKeys.Outcome(safeId)]</c> — this write is after the
    ///         try/finally and therefore always executes.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Security — local-function containment property (M3 fix).</strong>
    /// The author body is spliced verbatim inside an <c>async</c> local function.
    /// A <c>return;</c> statement in the author body returns from the local
    /// function only — not from the Roslyn submission delegate — so the engine's
    /// outcome write and all downstream step blocks execute normally.  A
    /// brace-injection body that closes the local function early produces orphaned
    /// <c>catch</c>/<c>finally</c> syntax and fails to compile
    /// (<c>ScriptCompilationException</c> / Inconclusive verdict), which is still
    /// caught.  The engine's outcome write is positioned after the <c>finally</c>
    /// of the outer <c>try</c> and therefore cannot be skipped by any author-body
    /// content that leaves the text compilable.
    /// </para>
    /// <para>
    /// The leading <c>await System.Threading.Tasks.Task.CompletedTask;</c> inside
    /// the local function guarantees the async function has an <c>await</c> (no
    /// CS1998 warning) and is always reached, even when the author body contains
    /// no <c>await</c> of its own.
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

        // 'file' is resolved and read here (compile time, once) rather than at bind
        // time, so that Bind stays a pure, non-throwing YAML→model projection and
        // Validate remains the single stage that reports a bad/missing path as a
        // clean ValidationFailure (→ Inconclusive) instead of an unhandled exception.
        // Validate already confirmed the file exists; a same-run TOCTOU race here is
        // an accepted, narrow edge case.
        var source = model.File is not null
            ? System.IO.File.ReadAllText(
                System.IO.Path.GetFullPath(System.IO.Path.Combine(ctx.SuiteDirectory, model.File)))
            : model.Code!;

        // The outcome key is engine-derived only (never from author input).
        // JsonSerializer.Serialize wraps it in double-quotes and escapes any
        // special characters, producing a safe C# string literal.
        var outcomeKeyLiteral = JsonSerializer.Serialize(VarKeys.Outcome(safeId));

        // Build the StatementBlock with a StringBuilder.
        // The author body is appended VERBATIM — no escaping, no interpolation hole.
        // Every other part of the wrapper is a fixed string literal appended around it.
        //
        // M3 fix — local-function containment: the author body is placed inside an
        // async local function (__body_<safeId>) so that a 'return;' in the author
        // code returns from the local function only, not from the Roslyn submission
        // delegate.  This ensures the engine's outcome write and all downstream step
        // blocks always execute.  A brace-injection that closes __body_ early produces
        // orphaned syntax → compile error → Inconclusive (still caught, still good).
        //
        // All engine-introduced locals carry the safeId suffix so that two
        // script.csharp steps in the same suite never collide:
        //   __sw_<safeId>    — Stopwatch
        //   __v_<safeId>     — Verdict
        //   __obs_<safeId>   — observation string
        //   __body_<safeId>  — async local function containing the author body
        //   __ex_<safeId>    — caught exception (catch-clause parameter)
        var sb = new StringBuilder();

        sb.Append("{\n");
        sb.Append("    var __sw_").Append(safeId).Append(" = System.Diagnostics.Stopwatch.StartNew();\n");
        sb.Append("    Vouchfx.Engine.Abstractions.Verdict __v_").Append(safeId)
          .Append(" = Vouchfx.Engine.Abstractions.Verdict.Pass;\n");
        sb.Append("    string? __obs_").Append(safeId).Append(" = null;\n");
        // Declare the async local function that contains the author body verbatim.
        // The leading 'await Task.CompletedTask;' suppresses CS1998 (no await in async
        // method) for author bodies that contain no await, and is always a no-op.
        sb.Append("    async System.Threading.Tasks.Task __body_").Append(safeId).Append("()\n");
        sb.Append("    {\n");
        sb.Append("        await System.Threading.Tasks.Task.CompletedTask;\n");
        sb.Append("        // ---- begin author code (spliced verbatim) ----\n");
        sb.Append(source);
        sb.Append("\n        // ---- end author code ----\n");
        sb.Append("    }\n");
        sb.Append("    try\n");
        sb.Append("    {\n");
        sb.Append("        await __body_").Append(safeId).Append("();\n");
        sb.Append("    }\n");
        sb.Append("    catch (System.Exception __ex_").Append(safeId).Append(")\n");
        sb.Append("    {\n");
        sb.Append("        __v_").Append(safeId)
          .Append(" = Vouchfx.Engine.Abstractions.Verdict.Fail;\n");
        sb.Append("        __obs_").Append(safeId)
          .Append(" = __ex_").Append(safeId).Append(".Message;\n");
        sb.Append("    }\n");
        sb.Append("    finally\n");
        sb.Append("    {\n");
        sb.Append("        __sw_").Append(safeId).Append(".Stop();\n");
        sb.Append("    }\n");
        sb.Append("    Vars[").Append(outcomeKeyLiteral)
          .Append("] = new Vouchfx.Engine.Abstractions.StepOutcome(__v_").Append(safeId)
          .Append(", __sw_").Append(safeId)
          .Append(".ElapsedMilliseconds, __obs_").Append(safeId).Append(");\n");
        sb.Append('}');

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: sb.ToString());
    }
}
