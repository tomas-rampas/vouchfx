// Tests for S05-B-02: SecretHelper.Source — the Secret_Helpers static class spliced
// into providers' CSX.
//
// Verifies:
//   1. Secret_Helpers.Source compiles standalone as a Roslyn script (fully-qualified
//      types; no 'using' context required).
//   2. At runtime Resolve(secrets, "x ${secret:env/A} y") calls the accessor with the
//      raw token and substitutes the revealed value.
//   3. Two copies of Secret_Helpers.Source dedupe to ONE via CsxAssembler (byte-identical).
//   4. Source contains no 'using var' (§13.3.1).
//
// The accessor is a recording fake that wraps a real SecretAccessor over an env
// resolver, so the test proves the helper invokes ISecretAccessor.Resolve(token) and
// surfaces the revealed value — without minting a SecretString directly (its ctor is
// internal to the Abstractions assembly).

using System;
using System.Collections.Generic;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Xunit;

namespace Platform.Sdk.Tests;

/// <summary>
/// Non-docker tests for <see cref="SecretHelper"/> and its splice into the CSX
/// pipeline (S05-B-02, §17).
/// </summary>
public sealed class SecretHelperEmitTests
{
    /// <summary>
    /// Records every reference passed to <see cref="Resolve"/> and delegates to a
    /// real accessor so the revealed value is genuine.
    /// </summary>
    private sealed class RecordingSecretAccessor : ISecretAccessor
    {
        private readonly ISecretAccessor _inner;

        public RecordingSecretAccessor(ISecretAccessor inner) => _inner = inner;

        public List<string> References { get; } = new();

        public SecretString Resolve(string reference)
        {
            References.Add(reference);
            return _inner.Resolve(reference);
        }
    }

    private static readonly IReadOnlyList<string> s_regexRefs = new[]
    {
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    // ── 1. Compiles standalone ────────────────────────────────────────────────

    [Fact]
    public void SecretHelperSource_CompilesStandalone()
    {
        var csx =
            SecretHelper.Source + "\n" +
            "Vars[\"result\"] = Secret_Helpers.Resolve(Secrets, \"no tokens here\");";

        // No env var is read because the template has no secret token.
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_regexRefs);

        Assert.NotNull(compiled);
    }

    // ── 2. Resolve calls the accessor and substitutes the revealed value ──────

    [Fact]
    public async Task Resolve_CallsAccessor_AndSubstitutesRevealedValue()
    {
        var envName = "VOUCHFX_SDK_SECRET_" + Guid.NewGuid().ToString("N");
        const string secretValue = "topsecret";
        Environment.SetEnvironmentVariable(envName, secretValue);
        try
        {
            var realAccessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var recording = new RecordingSecretAccessor(realAccessor);

            // Two tokens around literal text — both must resolve in one pass.
            var template = $"x ${{secret:env/{envName}}} y";
            var csx =
                SecretHelper.Source + "\n" +
                $"Vars[\"result\"] = Secret_Helpers.Resolve(Secrets, \"{template}\");";

            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_regexRefs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                recording);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            // The revealed value replaced the token; literal text preserved.
            Assert.Equal($"x {secretValue} y", vars["result"]);

            // The accessor was called with the RAW token, not the bare path.
            Assert.Contains($"${{secret:env/{envName}}}", recording.References);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 2b. ResolveTemplate single pass: no secret-reference INJECTION ─────────

    /// <summary>
    /// A <c>{placeholder}</c> whose runtime Vars value is the LITERAL text
    /// <c>${secret:env/INJECTED}</c> must NOT be secret-resolved: the single
    /// left-to-right pass never re-scans a substituted value, so the literal token
    /// passes through unchanged and the accessor is never asked to resolve it (§17).
    /// </summary>
    [Fact]
    public async Task ResolveTemplate_PlaceholderValueLooksLikeSecret_IsNotSecretResolved()
    {
        var realAccessor = new SecretAccessor(
            new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
        var recording = new RecordingSecretAccessor(realAccessor);

        var csx =
            SecretHelper.Source + "\n" +
            "Vars[\"result\"] = Secret_Helpers.ResolveTemplate(Secrets, Vars, \"v={inj}\");";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_regexRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // The placeholder's runtime value is itself a literal secret token.
            ["inj"] = "${secret:env/INJECTED}",
        };
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            recording);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // The literal token survives verbatim — it was NOT secret-resolved.
        Assert.Equal("v=${secret:env/INJECTED}", vars["result"]);

        // The accessor was never asked to resolve the injected token (no env read).
        Assert.DoesNotContain("${secret:env/INJECTED}", recording.References);
        Assert.Empty(recording.References);
    }

    // ── 2c. ResolveTemplate single pass: revealed secret is NOT re-substituted ──

    /// <summary>
    /// A secret whose REVEALED value contains <c>{x}</c> must NOT be corrupted by
    /// placeholder substitution: the single pass never re-scans the revealed value,
    /// so the brace text survives verbatim even when <c>Vars</c> has a key <c>x</c> (§17).
    /// </summary>
    [Fact]
    public async Task ResolveTemplate_RevealedSecretContainingBraces_IsNotSubstituted()
    {
        var envName = "VOUCHFX_SDK_SECRET_" + Guid.NewGuid().ToString("N");
        // The secret value embeds a placeholder-looking token {x}.
        const string secretValue = "abc{x}def";
        Environment.SetEnvironmentVariable(envName, secretValue);
        try
        {
            var realAccessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var recording = new RecordingSecretAccessor(realAccessor);

            var template = $"t=${{secret:env/{envName}}}";
            var csx =
                SecretHelper.Source + "\n" +
                $"Vars[\"result\"] = Secret_Helpers.ResolveTemplate(Secrets, Vars, \"{template}\");";

            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_regexRefs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // If the revealed value were re-scanned, {x} would become "REPLACED".
                ["x"] = "REPLACED",
            };
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                recording);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            // The revealed value (including its literal {x}) survives unchanged.
            Assert.Equal($"t={secretValue}", vars["result"]);
            Assert.Contains($"${{secret:env/{envName}}}", recording.References);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 3. Two copies dedupe to one via CsxAssembler ──────────────────────────

    [Fact]
    public void TwoCopiesOfSecretHelper_DedupeToOne()
    {
        // Two fragments each contributing Secret_Helpers.Source (byte-identical).
        var fragA = new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: new[] { SecretHelper.Source },
            StatementBlock: "{ }");
        var fragB = new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: new[] { SecretHelper.Source },
            StatementBlock: "{ }");

        var assembled = CsxAssembler.Assemble(new[] { ("a", fragA), ("b", fragB) });

        // The static class must appear exactly once after dedup (§13.3.1).
        var count = CountOccurrences(assembled.CsxSource, "static class Secret_Helpers");
        Assert.Equal(1, count);
    }

    // ── 4. No 'using var' ──────────────────────────────────────────────────────

    [Fact]
    public void SecretHelperSource_ContainsNoUsingVar()
    {
        Assert.DoesNotContain("using var", SecretHelper.Source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += token.Length;
        }

        return count;
    }
}
