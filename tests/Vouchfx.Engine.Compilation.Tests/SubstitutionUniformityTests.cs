// S07-B-02b — substitution-uniformity proof (non-docker).
//
// PURPOSE (the "single source of truth" proof):
//   Every Core provider that resolves author text routes it through ONE of two shared
//   helpers spliced from Vouchfx.Sdk:
//     • Secret_Helpers.ResolveTemplate(secrets, vars, template) — a SINGLE left-to-right
//       pass over the original template handling BOTH {placeholder} substitution AND
//       ${secret:source/path} resolution (http.rest path/headers/body; mq-publish &
//       mq-expect topic/key/payload/header/json values).
//     • Substitute_Helpers.Resolve(vars, template) — the {placeholder}-only sibling used
//       where secret resolution is intentionally separated from substitution
//       (db-assert.postgres parameter values and expect-row values; the query TEXT goes
//       through Substitute_Helpers.ResolveIdentifier with the same {placeholder} semantics
//       plus a SQL-identifier charset guard).
//
//   Because the helpers are byte-identical compile-time constants (CsxAssembler dedupes
//   them to one copy per suite), "which provider would call it" cannot change the result.
//   This file PROVES that uniformity directly: it feeds the SAME set of templates through
//   the real helper SOURCE (compiled as a Roslyn script, exactly as a provider emits it)
//   and asserts the resolved output is identical and correct for:
//     (a) a {placeholder}-only template,
//     (b) a ${secret:env/X}-only template,
//     (c) a mixed {p} + ${secret:env/X} template,
//     (d) an adjacent {p}${secret:env/X} template (no separator — the regex alternation
//         must split the two tokens cleanly with no overlap).
//   It also proves the {placeholder}-only subset of ResolveTemplate is byte-identical to
//   Substitute_Helpers.Resolve over the same template (the two grammars agree on
//   placeholders), so a provider that uses Substitute_Helpers.Resolve and one that uses
//   ResolveTemplate substitute placeholders identically.
//
// script.csharp BOUNDARY (Deliverable 4 — documented honestly, NOT pretended away):
//   script.csharp is the deliberate escape hatch. Its provider emits
//   RequiredHelpers = Array.Empty<string>() and splices the author's C# body VERBATIM
//   (ScriptCsharpProvider.Emit). It therefore does NOT participate in this uniform
//   substitution/redaction mechanism: there is no {placeholder} pass and no
//   ${secret:...} pass over author code, and SecretString is not auto-applied. The author
//   owns the exact bytes they write (they may read Vars / connection strings directly).
//   This is the known open MEDIUM follow-up (see sprint-05 memory + the §17 note routed to
//   technical-docs-writer). Do NOT add a script.csharp case to the uniformity table — it
//   would be a false claim. The ScriptCsharpBoundary_* test below LOCKS this contract so a
//   future change that silently starts substituting author code fails loudly here.
//
// All tests are non-docker: the helper SOURCE is compiled + run through the in-process
// collectible-ALC pipeline (RoslynScriptCompiler), and the env source is the real
// EnvironmentSecretResolver fed a per-test GUID-named variable.
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S07-B-02b: proves the shared substitution helpers (<c>Secret_Helpers.ResolveTemplate</c>
/// and <c>Substitute_Helpers.Resolve</c>) are the single source of truth for author-text
/// resolution, and documents the deliberate <c>script.csharp</c> non-participation boundary.
/// </summary>
public sealed class SubstitutionUniformityTests
{
    private const string PlaceholderValue = "alpha-42";
    private const string SecretValue = "s3cr3t-value-zzz";

    // Roslyn metadata refs the spliced helpers need (Regex, Convert, IDictionary,
    // ISecretAccessor / SecretString live in already-loaded BCL + Abstractions assemblies).
    private static readonly IReadOnlyList<string> s_refs = new[]
    {
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Convert).Assembly.Location,
        typeof(ISecretAccessor).Assembly.Location,
    };

    // ── The table: the SAME templates fed through the shared helper ───────────────

    /// <summary>
    /// The four template shapes that exercise every branch of the shared resolver, plus
    /// the expected resolved output given <c>{p} = "alpha-42"</c> and the per-test secret
    /// <c>${secret:env/&lt;name&gt;} = "s3cr3t-value-zzz"</c>.  The same rows are run
    /// through <c>Secret_Helpers.ResolveTemplate</c> below.  A provider only ever passes
    /// author text to this one helper, so these rows ARE the cross-provider contract.
    /// </summary>
    public static IEnumerable<object[]> UniformResolutionTable()
    {
        // template-shape                       , templateFormat ({SECRET} replaced per-test) , expected
        yield return new object[] { "placeholder-only", "/orders/{p}/items", $"/orders/{PlaceholderValue}/items" };
        yield return new object[] { "secret-only", "Bearer {SECRET}", $"Bearer {SecretValue}" };
        yield return new object[] { "mixed", "u={p}&t={SECRET}", $"u={PlaceholderValue}&t={SecretValue}" };
        yield return new object[] { "adjacent", "{p}{SECRET}", $"{PlaceholderValue}{SecretValue}" };
    }

    /// <summary>
    /// Each template shape, resolved through the REAL <c>Secret_Helpers.ResolveTemplate</c>
    /// source (compiled as a Roslyn script exactly as a provider splices it), produces the
    /// expected output.  Because this is the single helper every substituting provider
    /// calls, the resolved output is by construction identical regardless of which provider
    /// would invoke it — that is the uniformity guarantee.
    /// </summary>
    [Theory]
    [MemberData(nameof(UniformResolutionTable))]
    public async Task ResolveTemplate_SameTemplate_ResolvesUniformly(
        string shape,
        string templateFormat,
        string expected)
    {
        _ = shape; // documentary label only.

        var envName = "VOUCHFX_UNIFORMITY_" + System.Guid.NewGuid().ToString("N");
        var secretToken = $"${{secret:env/{envName}}}";
        var template = templateFormat.Replace("{SECRET}", secretToken, System.StringComparison.Ordinal);

        System.Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var resolved = await ResolveTemplateViaHelperAsync(template, envName);
            Assert.Equal(expected, resolved);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(envName, null);
        }
    }

    /// <summary>
    /// The <c>{placeholder}</c> subset of <c>Secret_Helpers.ResolveTemplate</c> is
    /// byte-identical to <c>Substitute_Helpers.Resolve</c> over the same template (both use
    /// <c>Convert.ToString</c> / invariant culture and empty-string-on-absent semantics).
    /// This is why a provider that uses <c>Substitute_Helpers.Resolve</c> (db-assert param /
    /// expect values) and one that uses <c>ResolveTemplate</c> (http.rest / mq-*) substitute
    /// placeholders identically — the two entry points agree on the placeholder grammar.
    /// </summary>
    [Theory]
    [InlineData("/orders/{p}/items")]
    [InlineData("u={p}&v={p}")]
    [InlineData("no-placeholders-here")]
    [InlineData("{p}{p}")]
    public async Task ResolveTemplate_PlaceholderSubset_MatchesSubstituteResolve(string template)
    {
        // No secret token in any of these templates, so ResolveTemplate's secret branch is
        // never taken — the two helpers must agree byte-for-byte.
        var viaResolveTemplate = await ResolveTemplateViaHelperAsync(template, envName: null);
        var viaSubstituteResolve = await SubstituteResolveViaHelperAsync(template);

        Assert.Equal(viaSubstituteResolve, viaResolveTemplate);
    }

    /// <summary>
    /// An absent <c>{placeholder}</c> resolves to the empty string (never throws, never
    /// leaks the token) in BOTH helpers — the shared empty-on-absent contract.
    /// </summary>
    [Fact]
    public async Task ResolveTemplate_AbsentPlaceholder_EmptyString_InBothHelpers()
    {
        const string template = "before-{missing}-after";
        var viaResolveTemplate = await ResolveTemplateViaHelperAsync(template, envName: null);
        var viaSubstituteResolve = await SubstituteResolveViaHelperAsync(template);

        Assert.Equal("before--after", viaResolveTemplate);
        Assert.Equal("before--after", viaSubstituteResolve);
    }

    // ── script.csharp boundary: NOT auto-substituted / NOT redacted ───────────────

    /// <summary>
    /// LOCKS the <c>script.csharp</c> non-participation boundary (Deliverable 4).
    /// <para>
    /// The provider emits NO shared helpers (<c>RequiredHelpers</c> is empty) and splices the
    /// author body verbatim, so author code is NOT routed through
    /// <c>Secret_Helpers.ResolveTemplate</c> / <c>Substitute_Helpers.Resolve</c>.  A
    /// <c>{placeholder}</c> or <c>${secret:...}</c> token written in author code therefore
    /// survives as LITERAL text (it is C# source, not a resolved template).  This is the
    /// intentional escape-hatch contract (§13 — no sandbox; the author is trusted) and the
    /// known open MEDIUM follow-up.  If a future change starts substituting author code, the
    /// helper-presence assertions below fail loudly.
    /// </para>
    /// </summary>
    [Fact]
    public void ScriptCsharpBoundary_EmitsNoSharedSubstitutionHelpers_AndSplicesVerbatim()
    {
        var provider = new ScriptCsharpProvider();
        // Author body deliberately CONTAINS a {placeholder}-shaped token and a
        // ${secret:...}-shaped token *as C# string content*. If the provider participated
        // in uniform substitution it would route this through a resolve helper — it must NOT.
        var model = new ScriptCsharpModel(
            Code: "var note = \"order {p} token ${secret:env/X}\"; Vars[\"note\"] = note;");
        var ctx = new StubCompileContext("verbatim-step");

        var fragment = provider.Emit(model, ctx);

        // 1. No shared substitution/secret helper is contributed by script.csharp.
        Assert.Empty(fragment.RequiredHelpers);

        // 2. The author body is spliced VERBATIM — the literal tokens survive untouched,
        //    and there is no resolve-call wrapping author text.
        Assert.Contains("order {p} token ${secret:env/X}", fragment.StatementBlock, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Secret_Helpers.ResolveTemplate", fragment.StatementBlock, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Substitute_Helpers.Resolve", fragment.StatementBlock, System.StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles the REAL <c>Secret_Helpers.ResolveTemplate</c> source (exactly as a provider
    /// splices it) plus a one-line driver that calls it with the supplied template, runs it
    /// through the isolated collectible-ALC pipeline, and returns the resolved string.  When
    /// <paramref name="envName"/> is non-null an <see cref="EnvironmentSecretResolver"/>-backed
    /// accessor is supplied so a <c>${secret:env/&lt;envName&gt;}</c> token resolves; otherwise
    /// a <see cref="NullSecretAccessor"/> is used (no secret token expected).
    /// </summary>
    private static async Task<string> ResolveTemplateViaHelperAsync(string template, string? envName)
    {
        // The driver writes the resolved value into Vars["__resolved"] so the test can read
        // it back. JsonSerializer.Serialize is NOT used for the template here because we want
        // the raw template text passed straight through — we build the literal manually with
        // a verbatim-safe escape (the templates contain no embedded double-quote).
        Assert.DoesNotContain("\"", template, System.StringComparison.Ordinal);

        var csx =
            SecretHelper.Source + "\n" +
            "Vars[\"__resolved\"] = Secret_Helpers.ResolveTemplate(Secrets, Vars, \"" + template + "\");";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_refs);

        var vars = new Dictionary<string, object?>(System.StringComparer.Ordinal)
        {
            ["p"] = PlaceholderValue,
        };

        ScriptGlobalVariables globals;
        if (envName is not null)
        {
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(System.StringComparer.Ordinal),
                accessor);
        }
        else
        {
            globals = new ScriptGlobalVariables(vars);
        }

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        return Assert.IsType<string>(vars["__resolved"]);
    }

    /// <summary>
    /// Compiles the REAL <c>Substitute_Helpers.Resolve</c> source and resolves the same
    /// template (placeholder-only path; no secret accessor needed).
    /// </summary>
    private static async Task<string> SubstituteResolveViaHelperAsync(string template)
    {
        Assert.DoesNotContain("\"", template, System.StringComparison.Ordinal);

        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"__resolved\"] = Substitute_Helpers.Resolve(Vars, \"" + template + "\");";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_refs);

        var vars = new Dictionary<string, object?>(System.StringComparer.Ordinal)
        {
            ["p"] = PlaceholderValue,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        return Assert.IsType<string>(vars["__resolved"]);
    }

    /// <summary>Minimal compile context with no captures.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(System.StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(System.StringComparer.Ordinal);
    }
}
