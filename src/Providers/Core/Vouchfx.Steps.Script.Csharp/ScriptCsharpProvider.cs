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
//
// Resource bound, NOT a crash-closer (see Validate below): script.csharp's body — the
// inline `code` text, or the `file` reference's ON-DISK SIZE (never its content; Validate
// checks FileInfo.Length only, so a `file:` pointing at a multi-GB file is rejected on size
// alone, without ever being read into memory) — is capped at 64 KiB. That is a sane size
// limit for a hand-authored test body; it is NOT a defence against a determined hostile
// author. RoslynScriptCompiler.CompileOnce can still crash or hang the process on a body
// well under this cap: a ~2 KB deeply-nested NON-bracket body (e.g. chained generic type
// arguments) overflows the native stack inside the BINDER during Compilation.Emit — parsing
// itself succeeds — and a short (~100-char) deeply-nested string-interpolation expression
// can HANG the parse inside CSharpScript.Create/GetCompilation, unboundedly (the compile
// budget's CancellationToken only bounds Emit — see RoslynScriptCompiler.CompileOnce — it
// never reaches the parse phase). A bracket/paren nesting-depth scan was tried here and
// removed: proven incomplete (it does not, and structurally cannot, count angle brackets,
// generics, or the many non-bracket constructs that also recurse the compiler), so it gave
// false confidence rather than real protection. Closing this off completely requires
// isolating the in-process compile so a crash/hang there cannot take the host process down
// with it — tracked separately as follow-up #276; until then, script.csharp is bounded
// only against accidental/pathological SIZE, not against a deliberately hostile body.
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
/// <para>
/// <strong>The compile itself is not hardened against a hostile author.</strong>
/// <see cref="Validate"/> applies a plain 64&#160;KiB size bound (see its remarks) — a
/// sane limit for a hand-authored body, not a security control. A small, deliberately
/// pathological body can still crash or hang <c>RoslynScriptCompiler.CompileOnce</c>
/// well under that size (see this file's header comment for specifics). Closing that off
/// requires running the in-process compile somewhere a crash cannot take the host process
/// down with it — out of scope for this provider, tracked separately.
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
          "description": "Runs an author-supplied C# snippet — given inline or as a path to an external .csx file — against the shared step context. Exactly one of 'code' or 'file' must be set.",
          "type": "object",
          "oneOf": [
            { "required": ["code"] },
            { "required": ["file"] }
          ],
          "properties": {
            "code": {
              "description": "Inline C# code block executed inside the compiled CSX submission.  Has access to the shared Vars dictionary.  Mutually exclusive with 'file'.  Capped at 64 KiB (a plain resource bound, not a security control).",
              "type": "string",
              "minLength": 1,
              "maxLength": 65536
            },
            "file": {
              "description": "Path to an external .csx file, resolved relative to the .e2e.yaml file's directory.  Read once at compile time and spliced verbatim, exactly like 'code'.  Mutually exclusive with 'code'.",
              "type": "string",
              "minLength": 1
            }
          }
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

    /// <summary>
    /// The maximum permitted size of a <c>script.csharp</c> body — the inline <c>code</c>
    /// text (measured in characters) or the <c>file</c> reference's ON-DISK size (measured
    /// in bytes, via <see cref="System.IO.FileInfo.Length"/> — the file's content is never
    /// read for this check). 64 KiB is a plain, generous resource bound: real author bodies
    /// run from a few hundred bytes to low single-digit KiB, so this only ever rejects
    /// pathological input.
    /// </summary>
    /// <remarks>
    /// This is a SIZE bound, not a security control. It does not, and cannot, close the
    /// in-process compiler crash/hang surface described in this file's header comment — a
    /// body comfortably under this limit can still crash or hang
    /// <c>RoslynScriptCompiler.CompileOnce</c>. A bracket-nesting-depth companion check was
    /// tried and removed: it could not, even in principle, catch the non-bracket
    /// constructs (e.g. chained generics) that actually recurse the compiler, so it gave
    /// false confidence rather than real protection.
    /// </remarks>
    private const int MaxBodyLength = 65536; // 64 KiB.

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

        if (hasCode)
        {
            var length = model.Code!.Length;
            if (length > MaxBodyLength)
            {
                return ValidationResult.Failure(
                    BuildSizeLimitMessage("'code'", length, "characters"));
            }
        }

        if (hasFile)
        {
            // GUARDED FOR EXACTLY THE REASON THE FileInfo.Length STAT BELOW IS, and this call
            // sat outside every try directly ABOVE the comment stating the rule it broke
            // (found in peer review of #488). Validate's contract is to NEVER throw — an
            // unhandled exception here surfaces as a provider fault rather than a clean
            // ValidationResult.Failure — and Path.GetFullPath is a throwing route:
            // ArgumentException, NotSupportedException, or PathTooLongException (an
            // IOException). Measured on net8.0: a declared path carrying an embedded NUL
            // raises `ArgumentException: Null character in path.`, which is the arm the
            // companion test drives.
            //
            // NOT A VERDICT CHANGE, checked before making it: an escaping throw was caught by
            // ProviderPipeline's own Validate guard and turned into a compile refusal, and a
            // ValidationResult.Failure is turned into a compile refusal too — both land on
            // Inconclusive (§12.1). What changes is the TEXT: an authoring fault naming the
            // author's own declared path, instead of a provider-defect report about a method
            // the author cannot fix.
            //
            // Type name only, never the resolved path — the same rule as both guards below.
            // Note the resolved path does not even exist as a value on this arm: the call that
            // would have produced it is the one that threw.
            string resolvedPath;
            try
            {
                resolvedPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(ctx.SuiteDirectory, model.File!));
            }
            catch (Exception ex) when (ex is ArgumentException
                or System.IO.IOException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                return ValidationResult.Failure(
                    $"script.csharp: could not resolve file '{model.File}', relative to the "
                    + $"suite directory: {ex.GetType().Name}");
            }

            if (!System.IO.File.Exists(resolvedPath))
            {
                // NO RESOLVED PATH IN THE MESSAGE (#357). `resolvedPath` is an absolute host
                // path, and this diagnostic ships to CI artefacts and dashboards — a wider
                // audience than whoever runs the suite. ScenarioRunner.ScrubDiagnostic applies
                // two scrub nets — ResolvedSecrets.Scrub (recorded secret VALUES) and
                // SecurityPathDisclosureLedger (security-material and seed paths; #375, widened
                // by #473) — and a script `file:` path is NEITHER, so nothing downstream
                // substitutes or redacts it. This message does reach a scrub chokepoint (the
                // authoring-fault event line takes both ledgers), but a net cannot replace what
                // was never recorded into it: omitting the resolved path here is the only guard,
                // exactly as #357 required.
                //
                // AND IT IS THE ONLY GUARD AVAILABLE HERE, structurally — #473 examined this site
                // and deliberately changed nothing. A provider assembly references only
                // Vouchfx.Sdk and Vouchfx.Engine.Abstractions; the ledger lives in
                // Vouchfx.Engine.Orchestration, so no provider can reach one to record into even
                // if it wanted to. That is a property of the assembly graph, not an omission, and
                // it is why this message must go on naming the declared path by construction.
                //
                // The declared form is the actionable half and the resolved form never was — the
                // same fix, with the same measured outcome, that slice D applied to
                // SecurityMaterialException's clientCert/clientKey/caCert messages. Naming the
                // directory the path resolves AGAINST keeps a relative path diagnosable without
                // disclosing where the file would have landed.
                return ValidationResult.Failure(
                    $"script.csharp: file '{model.File}' not found, relative to the suite directory.");
            }

            // Size-only check via FileInfo.Length — the content is deliberately NEVER read
            // here. Reading it (a prior revision of this guard did, in order to also scan
            // for nesting depth) would let a 'file:' reference pointing at an arbitrarily
            // large file (a multi-GB file, a device node, …) be pulled fully into memory
            // before any bound was applied — a denial-of-service surface in its own right.
            // Checking the on-disk length first rejects an oversized file on size alone,
            // with no read at all. Emit still reads the (now size-bounded) file content
            // exactly as before.
            //
            // Copilot review (#277): File.Exists just returned true, but the .Length stat
            // below can still throw — a permissions problem (UnauthorizedAccessException /
            // SecurityException), an I/O fault, or a racey delete/replace between the
            // Exists check and here (IOException covers FileNotFoundException and
            // DirectoryNotFoundException too). Validate's contract is to NEVER throw — an
            // unhandled exception here would surface as an engine crash instead of a clean
            // ValidationResult.Failure (→ Inconclusive, §12.1) — so the stat is guarded and
            // any failure is reported as an ordinary validation failure. The exception TYPE
            // NAME only is reported (never ex.Message): the message text is not vetted for
            // terminal-safety the way author-controlled text is elsewhere (DisplaySanitiser,
            // issue #266 Item 4), and the type name alone is already actionable.
            long length;
            try
            {
                length = new System.IO.FileInfo(resolvedPath).Length;
            }
            catch (Exception ex) when (ex is System.IO.IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                return ValidationResult.Failure(
                    $"script.csharp: could not stat file '{model.File}': {ex.GetType().Name}");
            }

            if (length > MaxBodyLength)
            {
                return ValidationResult.Failure(
                    BuildSizeLimitMessage("'file'", length, "bytes"));
            }
        }

        return ValidationResult.Success;
    }

    /// <summary>
    /// Builds the human-readable error message for a <c>script.csharp</c> body exceeding
    /// <see cref="MaxBodyLength"/>.
    /// </summary>
    /// <param name="fieldName">Either <c>"'code'"</c> or <c>"'file'"</c>.</param>
    /// <param name="length">The measured length (characters for <c>code</c>, bytes for <c>file</c>).</param>
    /// <param name="unit">Either <c>"characters"</c> or <c>"bytes"</c>, matching <paramref name="length"/>.</param>
    /// <remarks>
    /// Copilot review (#277, grammar): <paramref name="unit"/> is plural and correct for the
    /// measured COUNT ("72000 characters"), but a hyphenated adjective before "limit" wants the
    /// SINGULAR form ("a 65536-character limit", not "a 65536-characters limit") — the same
    /// count noun is used both ways in one sentence, so the singular form is derived here
    /// (trimming the trailing 's'; both recognised units are regular plurals) rather than
    /// threading a second parameter through every call site.
    /// </remarks>
    private static string BuildSizeLimitMessage(string fieldName, long length, string unit)
    {
        var singularUnit = unit.EndsWith('s') ? unit[..^1] : unit;
        return $"script.csharp: {fieldName} size {length} {unit} exceeds the "
            + $"{MaxBodyLength}-{singularUnit} limit (a plain resource bound, not a security "
            + "control — see this file's header comment); reduce its size or split the script.";
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
            ? ReadAuthorFile(model.File, ctx.SuiteDirectory)
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
        // Step-token cut (#232): the author body may observe __stepCt_<safeId> (declared by
        // the assembler's per-step wrapper, in scope here) and throw OperationCanceledException
        // when it fires.  Rethrow past the author-facing catch below so the wrapper classifies
        // it as Inconclusive(step-timeout) instead of this provider's own Fail mapping.
        sb.Append("    catch (System.OperationCanceledException) when (__stepCt_").Append(safeId).Append(".IsCancellationRequested)\n");
        sb.Append("    {\n");
        sb.Append("        throw;\n");
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

    /// <summary>
    /// Reads the author's external <c>.csx</c> body, re-raising any read failure as a
    /// diagnostic that names the <strong>declared</strong> path and never the resolved one
    /// (issue #488, in #357's shape).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THE RESOLVED PATH IS A LOCAL OF THIS METHOD AND LEAVES IT ONLY AS BYTES.</strong>
    /// <c>File.ReadAllText</c> quotes the absolute host path back in its own message —
    /// <c>UnauthorizedAccessException: Access to the path '&lt;resolved&gt;' is denied.</c>, or
    /// <c>IOException: The process cannot access the file '&lt;resolved&gt;' …</c> for a locked
    /// one. An <c>Emit</c> throw is caught by <c>ProviderPipeline</c> and folded into a compile
    /// refusal whose text <c>DescribeProviderFault</c> composes, and that reaches
    /// <c>--events</c>, the JUnit XML and the HTML report — an audience wider than whoever ran
    /// the suite. <strong>It is NOT <c>ScenarioRunner</c>'s
    /// <c>$"{ex.GetType().Name}: {ex.Message}"</c>:</strong> that spelling belongs to the
    /// scenario-level catch-all, the route an <c>Emit</c> throw cannot take because the pipeline
    /// catches it first. An earlier revision of this paragraph cited it and would have sent a
    /// checker to read the wrong method.
    /// </para>
    /// <para>
    /// <strong>AND THE DISCLOSURE IS CONDITIONAL, WHICH IS WHAT MAKES THIS GUARD NECESSARY
    /// RATHER THAN MERELY TIDY.</strong> Between the throw and the artefact sits
    /// <c>ProviderPipeline.ScrubSuiteDirectory</c>, which substitutes the literal text "the
    /// suite directory" for the resolved suite directory. For an ordinary in-suite
    /// <c>file: fixtures/x.csx</c> that already reduced the leak to
    /// <c>the suite directory\fixtures\x.csx</c> — incidental cover, not a guarantee, since it
    /// depends on a scrub in another assembly that knows nothing about this read.
    /// <strong>Where it does NOT apply, and this guard is the only protection, is a declared
    /// path that resolves OUTSIDE the suite directory</strong> — an unbounded substring replace
    /// finds no match, and the full host path ships. Nothing refuses such a path: <c>file</c>
    /// carries <c>minLength: 1</c> and no <c>pattern</c> in this provider's own schema fragment
    /// above, and <c>Validate</c> performs an existence check and a size check with no
    /// containment check. Both cases are driven by
    /// <c>Emit_FileUnreadable_DiagnosticNamesDeclaredPathNeverResolvedPath</c>'s two rows.
    /// </para>
    /// <para>
    /// <strong>A SECOND, UNPLANNED BENEFIT, recorded because it is a reason not to "simplify"
    /// the thrown type back to the original.</strong> <c>DescribeProviderFault</c> chooses its
    /// attribution sentence from <c>IsEnvironmentalCondition</c>, which is
    /// <c>cause is IOException or UnauthorizedAccessException</c>. The two path-shape routes
    /// through the guard below — <see cref="ArgumentException"/> and
    /// <see cref="NotSupportedException"/> — would fall to the <c>else</c> arm and be reported
    /// as <c>"This is a defect in the provider (ScriptCsharpProvider)"</c>, which is a false
    /// accusation for a path the AUTHOR wrote. Re-raising as
    /// <see cref="System.IO.IOException"/> lands every route in the non-accusatory
    /// filesystem-condition arm instead, so the change improves attribution as well as
    /// disclosure. Narrowing the thrown type would give that back.
    /// </para>
    /// <para>
    /// <strong>NAMING THE DECLARED PATH IS THE ONLY GUARD AVAILABLE, structurally</strong> — the
    /// same reason <c>Validate</c>'s not-found and stat guards give a few dozen lines above. #473
    /// examined this provider and wrote that reason INTO those guards, judging the provider
    /// "already compliant" and filing the rest as #488; that judgement was true of the messages
    /// <c>Validate</c> composes and not of the read below, which is the gap being closed here. A
    /// provider assembly references only <c>Vouchfx.Sdk</c> and
    /// <c>Vouchfx.Engine.Abstractions</c>; <c>SecurityPathDisclosureLedger</c> lives in
    /// <c>Vouchfx.Engine.Orchestration</c>, so no provider can record a declared/resolved pair
    /// into one even if it wanted to, and a scrub net cannot substitute what was never recorded.
    /// That is a property of the assembly graph, not an omission.
    /// </para>
    /// <para>
    /// <strong>NO INNER EXCEPTION, AND THAT IS LOAD-BEARING RATHER THAN TIDINESS.</strong>
    /// <c>ProviderPipeline.DescribeProviderFault</c> WALKS the thrown exception's inner chain
    /// and appends each message, precisely so a provider that wraps its real failure does not
    /// hide the cause. Attaching the original here would therefore put the BCL's
    /// resolved-path message straight back into the artefact this guard exists to keep it out
    /// of. The exception TYPE NAME is reported instead — the same trade
    /// <c>Validate</c>'s stat guard already makes, and it is the actionable half: an author
    /// reading <c>UnauthorizedAccessException</c> against their own declared path knows what to
    /// check.
    /// </para>
    /// <para>
    /// The catch is the IO family NAMED, never a bare <c>catch (Exception)</c>: an
    /// <c>OutOfMemoryException</c> raised through this frame is not "the file could not be
    /// read" and must not be relabelled as one. The full route list, matching the catch arm for
    /// arm: <c>Path.GetFullPath</c> raises <see cref="ArgumentException"/> (invalid characters),
    /// <see cref="NotSupportedException"/>, and <c>PathTooLongException</c> — which is an
    /// <see cref="System.IO.IOException"/>; <c>File.ReadAllText</c> raises
    /// <see cref="UnauthorizedAccessException"/>, a plain <see cref="System.IO.IOException"/>
    /// for a locked file or one deleted under it, and
    /// <see cref="System.Security.SecurityException"/> where a caller lacks the demanded
    /// permission. The resolve sits INSIDE the guard with the read precisely because the first
    /// three of those come from it, and its message is no more vetted than the read's.
    /// </para>
    /// </remarks>
    private static string ReadAuthorFile(string declaredPath, string suiteDirectory)
    {
        try
        {
            return System.IO.File.ReadAllText(
                System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(suiteDirectory, declaredPath)));
        }
        catch (Exception ex) when (ex is System.IO.IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            throw new System.IO.IOException(
                $"script.csharp: could not read file '{declaredPath}', relative to the suite "
                + $"directory: {ex.GetType().Name}");
        }
    }
}
