// Example.Steps.Echo — EchoTextProvider (worked example, S10-F-01).
//
// ─────────────────────────────────────────────────────────────────────────────────
// READ ME FIRST — this is the SECOND worked-example provider (after hello.console).
// It exists to PROVE, end to end, that an OUTSIDE CONTRIBUTOR can author a non-Core
// provider against the frozen v1 Platform.Sdk contract and exercise it through the
// published Platform.Sdk.Testing harness — WITHOUT Docker.
// ─────────────────────────────────────────────────────────────────────────────────
//
// `echo.text` is the smallest provider that demonstrates {placeholder} substitution:
// it resolves its `text` field against the shared `Vars` global at execution time
// (the same mechanism the Core http.rest provider uses for its `path`) and asserts the
// resolved text equals a constant `expect`.  It has NO infrastructure dependency, so the
// integration fixture (Example.Steps.Echo.Tests) runs it end to end WITHOUT Docker.
//
// The four steps a contributor takes are exactly:
//   1. Add a project that references ONLY Platform.Sdk (this csproj).
//   2. Define a strongly-typed model record : IStepModel (EchoTextModel).
//   3. Implement the v1 contract on one [StepProvider]-decorated class (this file):
//        IStepProvider + IStepBinder<T> + IStepValidator<T> + IStepCompiler<T>.
//   4. The reflective StepKindRegistry discovers it at startup — no registration code.
//
// §5.6 ASSEMBLY-GRAPH HYGIENE: this assembly uses the NON-reserved namespace
// `Example.Steps.Echo`.  The `Platform.Steps.*` and `Platform.Engine.*` namespaces are
// RESERVED for the engine and its Core providers; a customer DLL declaring them is
// refused at startup.  A real provider you ship must likewise pick its own namespace.
//
// §5 MEMORY MODEL: this provider takes NO reference to Platform.Engine.Abstractions.
// The emitted CSX reaches the run environment ONLY through the engine-injected `Vars`
// global and refers to engine types (StepOutcome, Verdict, VarKeys) by fully-qualified
// name — the engine already references Platform.Engine.Abstractions when it compiles the
// assembled script, so no static handle from this provider bridges the collectible
// AssemblyLoadContext boundary.

using System.Text.Json;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Example.Steps.Echo;

/// <summary>
/// Worked-example provider for the <c>echo.text</c> step kind — a trivial,
/// dependency-free step that resolves a (possibly <c>{placeholder}</c>-bearing) text
/// against the <c>Vars</c> global and asserts it equals a constant.
/// </summary>
/// <remarks>
/// <para>
/// This class is the second teaching template for authoring a <strong>non-Core</strong>
/// provider against the frozen v1 <see cref="Platform.Sdk"/> contract (§13).  It
/// implements the four mandatory provider interfaces on a single
/// <c>[StepProvider]</c>-decorated class: <see cref="IStepProvider"/> (identity),
/// <see cref="IStepBinder{TModel}"/> (YAML → model + schema fragment),
/// <see cref="IStepValidator{TModel}"/> (model rules), and
/// <see cref="IStepCompiler{TModel}"/> (model → <see cref="CsxFragment"/>).  It does
/// <em>not</em> implement the optional <see cref="IResourceContributor{TModel}"/> because
/// the step needs no infrastructure — that omission is exactly why the fixture runs
/// without Docker.
/// </para>
/// <para>
/// <strong>What this example adds over <c>hello.console</c>:</strong> the <c>text</c>
/// field is a TEMPLATE resolved at execution time via the shared
/// <c>Substitute_Helpers.Resolve(Vars, …)</c> helper (sourced byte-identically from
/// <see cref="SubstituteHelper.Source"/> and appended to
/// <see cref="CsxFragment.RequiredHelpers"/>, exactly as the Core <c>http.rest</c>
/// provider does for its <c>path</c>).  Cross-step state therefore threads forward
/// through <c>Vars</c>: a <c>{greeting}</c> token in <c>text</c> resolves to
/// <c>Vars["greeting"]</c> at runtime (or the empty string when that key is absent).
/// </para>
/// <para>
/// <strong>CsxFragment composition rules observed (§13.3.1):</strong>
/// <list type="bullet">
///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings only.</item>
///   <item><see cref="CsxFragment.RequiredHelpers"/> — one nested <c>static</c> class
///         whose name is PREFIXED with the provider id (<c>EchoText_Helpers</c>) so it
///         cannot collide with another provider's helper, PLUS the shared
///         <c>Substitute_Helpers</c> source (the assembler de-duplicates it).</item>
///   <item><see cref="CsxFragment.StatementBlock"/> — exactly one brace-enclosed block,
///         built as a C# 11 double-dollar raw string (<c>$$"""…"""</c>): a single brace is
///         a literal brace in the emitted CSX, and <c>{{hole}}</c> is an interpolation
///         hole the emitter fills.</item>
///   <item>No <c>using var</c> (illegal in a Roslyn script body) — plain <c>var</c> only.</item>
///   <item>Step ids are sanitised via <see cref="CsxFragment.SanitiseId"/> before being
///         spliced into emitted identifiers.</item>
///   <item>Author <c>text</c> and <c>expect</c> values are escaped at EMIT time via
///         <see cref="JsonSerializer.Serialize"/> (this runs in the provider, which
///         references <c>System.Text.Json</c>) so quotes / braces / backslashes become
///         safe C# literals. The runtime observation string is built by the dependency-free
///         <c>EchoText_Helpers.JsonEscape</c> helper — not <c>JsonSerializer.Serialize</c>
///         — because <c>System.Text.Json</c> is not in the minimal emitted-CSX reference set
///         (per FRICTION-LOG F2).</item>
///   <item>Cross-step state passes ONLY through the <c>Vars</c> global.</item>
/// </list>
/// </para>
/// </remarks>
[StepProvider]
public sealed class EchoTextProvider
    : IStepProvider,
      IStepBinder<EchoTextModel>,
      IStepValidator<EchoTextModel>,
      IStepCompiler<EchoTextModel>
{
    // Namespace strings the emitted block needs.  Bare strings only (§13.3.1) — the
    // engine emits the `using` lines and de-duplicates across all providers.
    //
    // FRICTION-LOG F2 (see README): the emitted body deliberately depends ONLY on types
    // the engine's MINIMAL Roslyn base reference set always provides (System.Private.CoreLib /
    // System.Runtime / System.Collections / System.Text.RegularExpressions +
    // Platform.Engine.Abstractions).  System.Text.Json is NOT in that minimal set, and this
    // dependency-free provider does NOT implement the optional ICompileReferenceContributor
    // (the single-step harness does not run that stage anyway), so the emitted block must
    // avoid System.Text.Json at RUNTIME.  The structured observation is therefore escaped by
    // a tiny, dependency-free helper (EchoText_Helpers) rather than via JsonSerializer.Serialize
    // — exactly how the hello.console template keeps its emitted body inside the minimal
    // reference set.
    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Diagnostics",
        "Platform.Engine.Abstractions",
    };

    // ── IStepProvider ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("echo", "text");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<EchoTextModel> ─────────────────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment describing the <c>echo.text</c> provider's OWN
    /// fields (<c>text</c>, <c>expect</c>).
    /// </summary>
    /// <remarks>
    /// Both <c>text</c> and <c>expect</c> are <c>required</c>: <c>echo.text</c> always
    /// makes a real assertion, so there is no "bare echo" mode.  The fragment does NOT
    /// declare the <c>type</c> discriminator — the engine's <c>SchemaComposer</c> derives
    /// that from <see cref="Kind"/> and injects it as an <c>if</c>/<c>then</c> clause, so
    /// a provider can never misdeclare its own key (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["text", "expect"],
          "properties": {
            "text": {
              "description": "The text the step echoes at execution time. May contain {placeholder} tokens resolved against Vars.",
              "type": "string"
            },
            "expect": {
              "description": "The constant the placeholder-resolved text is asserted to equal.",
              "type": "string"
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public EchoTextModel Bind(YamlNode node, IBindingContext ctx)
    {
        // BIND→VALIDATE CONTRACT (§13): Bind only SHAPES YAML into the model; it does not
        // reject input.  A non-mapping node yields an empty model.  Presence of the `text`
        // and `expect` keys is enforced by the composed JSON Schema (both are `required`),
        // so a step that omits one is rejected at schema validation — before Bind even
        // runs in the engine.  Keep rejection out of Bind (mirror HelloConsoleProvider).
        if (node is not YamlMappingNode mapping)
            return new EchoTextModel(Text: string.Empty, Expect: string.Empty);

        var text = ReadScalar(mapping, "text") ?? string.Empty;
        var expect = ReadScalar(mapping, "expect") ?? string.Empty;

        return new EchoTextModel(Text: text, Expect: expect);
    }

    private static string? ReadScalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value)
        && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    // ── IStepValidator<EchoTextModel> ──────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately minimal.  The composed JSON Schema already enforces that both
    /// <c>text</c> and <c>expect</c> are present, and an EMPTY resolved <c>text</c> is a
    /// legitimate runtime outcome (a <c>{placeholder}</c> whose key is absent resolves to
    /// the empty string — see the harness placeholder fixture), so Validate must not fail
    /// on an empty <see cref="EchoTextModel.Text"/>.  There is nothing further to check at
    /// the model level: the real assertion (text == expect) is made at execution time
    /// inside the emitted block.
    /// </remarks>
    public ValidationResult Validate(EchoTextModel model, IProjectContext ctx)
        => ValidationResult.Success;

    // ── IStepCompiler<EchoTextModel> ───────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Emits a single statement block that, AT RUNTIME:
    /// <list type="number">
    ///   <item>starts a <c>Stopwatch</c>;</item>
    ///   <item>resolves the <c>text</c> TEMPLATE against <c>Vars</c> via the shared
    ///         <c>Substitute_Helpers.Resolve</c> helper, so any <c>{placeholder}</c>
    ///         token threads forward cross-step state (B-03 mechanism);</item>
    ///   <item>compares the resolved text to the constant <c>expect</c> via the
    ///         provider-prefixed helper <c>EchoText_Helpers.Check</c>
    ///         (<see cref="System.StringComparison.Ordinal"/>);</item>
    ///   <item>builds a structured observation that CONTAINS the resolved text (escaped at
    ///         runtime by the dependency-free <c>EchoText_Helpers.Observe</c>);</item>
    ///   <item>writes a <c>StepOutcome</c> (Pass/Fail + duration + observation) to
    ///         <c>Vars</c> under the engine's canonical outcome key, which the runner
    ///         reads back after the isolated run.</item>
    /// </list>
    /// The <c>text</c> template and <c>expect</c> constant are escaped at EMIT time via
    /// <see cref="JsonSerializer.Serialize"/> (this runs in the provider) so author text
    /// containing quotes, braces or backslashes becomes a SAFE C# string literal — never
    /// breaking out of the emitted code.  Unlike <c>hello.console</c> (which builds its
    /// observation entirely at emit time), the observation here MUST be built at runtime
    /// via the dependency-free <c>EchoText_Helpers.JsonEscape</c> helper because the echoed
    /// text is not known until <c>Substitute_Helpers.Resolve</c> has run against the live
    /// <c>Vars</c>.
    /// </remarks>
    public CsxFragment Emit(EchoTextModel model, ICompileContext ctx)
    {
        // Hyphens are legal in YAML step ids but illegal in C# identifiers — sanitise
        // before splicing into emitted variable names (§13.3.1).
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Emit author-supplied text as JSON-escaped C# string literals (defence against
        // quotes / braces / backslashes corrupting the emitted source).
        //
        // CRITICAL: we are inside a $$"""…""" block below, so {{expr}} is the
        // interpolation hole and a lone {placeholder} inside textLiteral passes through
        // VERBATIM as literal text — it is NOT resolved at emit time.  Resolution happens
        // at RUNTIME via Substitute_Helpers.Resolve(Vars, …), so cross-step state in Vars
        // is threaded in (mirrors http.rest's path handling, B-03).  `expect` is a
        // constant and is compared verbatim (no substitution per the model's intent).
        var textTemplateLiteral = JsonSerializer.Serialize(model.Text);
        var expectLiteral = JsonSerializer.Serialize(model.Expect);

        // The provider-id-prefixed nested static helper (§13.3.1).  The engine
        // de-duplicates helpers by declared class name, so EVERY echo.text step in a
        // suite must emit byte-IDENTICAL helper source — therefore the helper must carry
        // NO step-specific data (all per-step values are passed in as arguments).
        const string helper = """
            static class EchoText_Helpers
            {
                // Returns true when the resolved text matches the expectation (Ordinal).
                public static bool Check(string text, string expect)
                    => string.Equals(text, expect, System.StringComparison.Ordinal);

                // Builds a structured observation containing the resolved text.  The value
                // is escaped by JsonEscape below (a tiny, dependency-free escaper) so any
                // quote / backslash / control character in the echoed value cannot corrupt
                // the JSON — without pulling System.Text.Json into the minimal emitted
                // reference set (FRICTION-LOG F2).
                public static string Observe(string text, string expect)
                    => "{\"text\":\"" + JsonEscape(text) + "\",\"expect\":\"" + JsonEscape(expect) + "\"}";

                // Minimal JSON string-body escaper (escapes the quote, backslash and the
                // C0 control characters JSON requires; emits \uXXXX for the rest).  This is
                // intentionally tiny — a portable provider should not assume a reference the
                // minimal compile path does not guarantee.
                private static string JsonEscape(string value)
                {
                    var sb = new System.Text.StringBuilder(value.Length + 2);
                    foreach (var ch in value)
                    {
                        switch (ch)
                        {
                            case '"':  sb.Append("\\\""); break;
                            case '\\': sb.Append("\\\\"); break;
                            case '\b': sb.Append("\\b");  break;
                            case '\f': sb.Append("\\f");  break;
                            case '\n': sb.Append("\\n");  break;
                            case '\r': sb.Append("\\r");  break;
                            case '\t': sb.Append("\\t");  break;
                            default:
                                if (ch < ' ')
                                    sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                                else
                                    sb.Append(ch);
                                break;
                        }
                    }
                    return sb.ToString();
                }
            }
            """;

        // The statement block — one brace-enclosed block, C# 11 double-dollar raw string.
        // With $$"""…""", a single { / } is a LITERAL brace in the emitted CSX (so this
        // block's own braces pass through verbatim) and {{hole}} is an interpolation hole.
        //
        // Engine-introduced locals carry the safeId suffix so two echo.text steps in one
        // suite never collide.  Engine types are referenced by fully-qualified name (this
        // provider does not reference Platform.Engine.Abstractions; the engine does when
        // it compiles the assembled script).  Cross-step state is read/written ONLY
        // through `Vars`.
        var block =
            $$"""
            {
                var __sw_{{safeId}} = System.Diagnostics.Stopwatch.StartNew();

                // Resolve the {placeholder} tokens in `text` against Vars at RUNTIME — the
                // same shared helper the Core http.rest provider uses for its `path`.  An
                // absent key resolves to the empty string (a harmless, well-defined miss).
                var __text_{{safeId}} = Substitute_Helpers.Resolve(Vars, {{textTemplateLiteral}});
                var __expect_{{safeId}} = {{expectLiteral}};

                // Record the resolved (echoed) text into Vars (state-via-Vars): the step's
                // visible side effect, observable by a later step or the report.
                Vars["echo::{{safeId}}"] = __text_{{safeId}};

                var __pass_{{safeId}} =
                    EchoText_Helpers.Check(__text_{{safeId}}, __expect_{{safeId}});
                __sw_{{safeId}}.Stop();

                var __verdict_{{safeId}} = __pass_{{safeId}}
                    ? Platform.Engine.Abstractions.Verdict.Pass
                    : Platform.Engine.Abstractions.Verdict.Fail;

                // Build the structured observation at RUNTIME (the echoed text is only known
                // once Substitute_Helpers.Resolve has run), then write the outcome under the
                // engine's canonical key; the runner reads it back after the isolated run.
                var __observation_{{safeId}} =
                    EchoText_Helpers.Observe(__text_{{safeId}}, __expect_{{safeId}});

                Vars[Platform.Engine.Abstractions.VarKeys.Outcome("{{safeId}}")] =
                    new Platform.Engine.Abstractions.StepOutcome(
                        __verdict_{{safeId}},
                        __sw_{{safeId}}.ElapsedMilliseconds,
                        __observation_{{safeId}});
            }
            """;

        // Build the helpers list: EchoText_Helpers + Substitute_Helpers (B-03).  The
        // Substitute_Helpers source is byte-identical across providers — deduplication is
        // handled by CsxAssembler (mirrors HttpRestProvider appending SubstituteHelper.Source).
        var helpers = new List<string>
        {
            helper,
            SubstituteHelper.Source,
        };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }
}
