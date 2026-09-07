// S03-B-04 — CsxAssembler tests.
//
// Validates the production CSX splice/dedup logic promoted from the test-helper
// pattern (seen in ReferenceProviderEndToEndTests / HttpRestEmitTests) into the
// permanent CsxAssembler type in Vouchfx.Engine.Compilation.
//
// Covers:
//   • using dedup with first-seen order preserved
//   • identical helper dedup (keep one copy)
//   • same helper class name / different body → CsxAssemblyException
//   • statement blocks concatenated in step order
//   • inline-using guard (bare namespaces only)
//   • no "using var" in assembled output
//   • end-to-end: assembled script compiles once and runs via collectible ALC
//   • CompileScenario sugar helper round-trip
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S03-B-04: unit tests for <see cref="CsxAssembler"/> and the
/// <see cref="RoslynScriptCompiler.CompileScenario"/> convenience helper.
/// </summary>
public sealed class CsxAssemblerTests
{
    // ── static arrays shared across tests (avoids CA1861) ─────────────────────

    private static readonly string[] SystemOnly = new[] { "System" };
    private static readonly string[] SystemAndText = new[] { "System", "System.Text" };
    private static readonly string[] SystemAndLinq = new[] { "System", "System.Linq" };
    private static readonly string[] AlphaHelpers = new[] { "internal static class Alpha_Helpers { }" };
    private static readonly string[] BetaHelpers = new[] { "internal static class Beta_Helpers { }" };
    private static readonly string[] EmptyStrings = Array.Empty<string>();

    private static readonly string[] SharedHelperArray = new[]
    {
        "internal static class Shared_Helpers { public static int Echo(int v) => v; }",
    };

    private static readonly string[] FooHelperV1Array = new[]
    {
        "internal static class Foo_Helpers { public static int A() => 1; }",
    };

    private static readonly string[] FooHelperV2Array = new[]
    {
        "internal static class Foo_Helpers { public static int B() => 2; }",
    };

    // Inline using directive (violates §13.3.1 — not a bare namespace).
    private static readonly string[] InlineUsingArray = new[] { "using System;" };

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal <see cref="CsxFragment"/> that writes <c>1</c> into
    /// <c>Vars[&quot;step_&lt;sanitisedId&gt;&quot;]</c> and declares a tiny prefixed
    /// helper class so the helper-dedup paths are exercised as well.
    /// </summary>
    private static CsxFragment MakeFragment(
        string stepId,
        string helperClassName,
        IReadOnlyList<string>? extraUsings = null)
    {
        var sanitised = CsxFragment.SanitiseId(stepId);
        var usings = new List<string> { "System", "System.Collections.Generic" };
        if (extraUsings is not null)
            usings.AddRange(extraUsings);

        // The statement block uses the $$"""…""" raw-string form (C# 11, §13.3.1).
        // Single { / } = literal brace; {{expr}} = interpolation hole.
        var block = $$"""
            {
                Vars["step_{{sanitised}}"] = 1;
            }
            """;

        var helper = $"internal static class {helperClassName} {{ public static int Echo(int v) => v; }}";

        return new CsxFragment(
            RequiredUsings: usings,
            RequiredHelpers: new[] { helper },
            StatementBlock: block);
    }

    // ── Assemble_MultipleSteps_DedupsUsings_Ordered ────────────────────────────

    /// <summary>
    /// Two fragments share <c>System</c> and each adds a unique using.
    /// The assembled source must contain each <c>using X;</c> exactly once,
    /// in first-seen order.
    /// </summary>
    [Fact]
    public void Assemble_MultipleSteps_DedupsUsings_Ordered()
    {
        var frag1 = new CsxFragment(
            RequiredUsings: SystemAndText,
            RequiredHelpers: AlphaHelpers,
            StatementBlock: "{ }");

        var frag2 = new CsxFragment(
            RequiredUsings: SystemAndLinq,      // "System" is a duplicate
            RequiredHelpers: BetaHelpers,
            StatementBlock: "{ }");

        var steps = new (string StepId, CsxFragment Fragment)[]
        {
            ("step-a", frag1),
            ("step-b", frag2),
        };

        var assembled = CsxAssembler.Assemble(steps);

        // Each using appears exactly once.
        Assert.Equal(1, CountOccurrences(assembled.CsxSource, "using System;"));
        Assert.Equal(1, CountOccurrences(assembled.CsxSource, "using System.Text;"));
        Assert.Equal(1, CountOccurrences(assembled.CsxSource, "using System.Linq;"));

        // First-seen order: System before System.Text before System.Linq.
        var idxSystem = assembled.CsxSource.IndexOf("using System;", StringComparison.Ordinal);
        var idxText = assembled.CsxSource.IndexOf("using System.Text;", StringComparison.Ordinal);
        var idxLinq = assembled.CsxSource.IndexOf("using System.Linq;", StringComparison.Ordinal);

        Assert.True(idxSystem < idxText, "System must appear before System.Text (first-seen order).");
        Assert.True(idxText < idxLinq, "System.Text must appear before System.Linq (first-seen order).");
    }

    // ── Assemble_DedupsIdenticalHelpers ───────────────────────────────────────

    /// <summary>
    /// Two fragments with a byte-identical helper class → the class appears once.
    /// </summary>
    [Fact]
    public void Assemble_DedupsIdenticalHelpers()
    {
        var frag1 = new CsxFragment(
            RequiredUsings: SystemOnly,
            RequiredHelpers: SharedHelperArray,
            StatementBlock: "{ }");

        var frag2 = new CsxFragment(
            RequiredUsings: SystemOnly,
            RequiredHelpers: SharedHelperArray,     // identical source
            StatementBlock: "{ }");

        var assembled = CsxAssembler.Assemble(new (string, CsxFragment)[]
        {
            ("s1", frag1),
            ("s2", frag2),
        });

        Assert.Equal(1, CountOccurrences(assembled.CsxSource, "static class Shared_Helpers"));
    }

    // ── Assemble_SameHelperClassNameDifferentBody_Throws ──────────────────────

    /// <summary>
    /// Two fragments declare <c>static class Foo_Helpers</c> with different bodies.
    /// This is a provider collision; the assembler must throw
    /// <see cref="CsxAssemblyException"/>.
    /// </summary>
    [Fact]
    public void Assemble_SameHelperClassNameDifferentBody_Throws()
    {
        var frag1 = new CsxFragment(
            RequiredUsings: SystemOnly,
            RequiredHelpers: FooHelperV1Array,
            StatementBlock: "{ }");

        var frag2 = new CsxFragment(
            RequiredUsings: SystemOnly,
            RequiredHelpers: FooHelperV2Array,
            StatementBlock: "{ }");

        Assert.Throws<CsxAssemblyException>(
            () => CsxAssembler.Assemble(new (string, CsxFragment)[]
            {
                ("s1", frag1),
                ("s2", frag2),
            }));
    }

    // ── Assemble_ConcatenatesStatementBlocksInOrder ───────────────────────────

    /// <summary>
    /// The assembled source must contain each block in the input step order.
    /// Sentinel comments inside each block are used as markers.
    /// </summary>
    [Fact]
    public void Assemble_ConcatenatesStatementBlocksInOrder()
    {
        var frag1 = new CsxFragment(
            RequiredUsings: EmptyStrings,
            RequiredHelpers: EmptyStrings,
            StatementBlock: "{ /* BLOCK_ONE */ }");

        var frag2 = new CsxFragment(
            RequiredUsings: EmptyStrings,
            RequiredHelpers: EmptyStrings,
            StatementBlock: "{ /* BLOCK_TWO */ }");

        var frag3 = new CsxFragment(
            RequiredUsings: EmptyStrings,
            RequiredHelpers: EmptyStrings,
            StatementBlock: "{ /* BLOCK_THREE */ }");

        var assembled = CsxAssembler.Assemble(new (string, CsxFragment)[]
        {
            ("s1", frag1),
            ("s2", frag2),
            ("s3", frag3),
        });

        var src = assembled.CsxSource;
        var idx1 = src.IndexOf("BLOCK_ONE", StringComparison.Ordinal);
        var idx2 = src.IndexOf("BLOCK_TWO", StringComparison.Ordinal);
        var idx3 = src.IndexOf("BLOCK_THREE", StringComparison.Ordinal);

        Assert.True(idx1 >= 0, "BLOCK_ONE not found.");
        Assert.True(idx2 >= 0, "BLOCK_TWO not found.");
        Assert.True(idx3 >= 0, "BLOCK_THREE not found.");
        Assert.True(idx1 < idx2, "BLOCK_ONE must precede BLOCK_TWO.");
        Assert.True(idx2 < idx3, "BLOCK_TWO must precede BLOCK_THREE.");
    }

    // ── Assemble_RejectsInlineUsingInRequiredUsings ───────────────────────────

    /// <summary>
    /// A <see cref="CsxFragment.RequiredUsings"/> entry of <c>"using System;"</c>
    /// is an inline directive, not a bare namespace.  The assembler must throw
    /// <see cref="CsxAssemblyException"/>.
    /// </summary>
    [Fact]
    public void Assemble_RejectsInlineUsingInRequiredUsings()
    {
        var badFrag = new CsxFragment(
            RequiredUsings: InlineUsingArray,          // violates §13.3.1
            RequiredHelpers: EmptyStrings,
            StatementBlock: "{ }");

        Assert.Throws<CsxAssemblyException>(
            () => CsxAssembler.Assemble(new (string, CsxFragment)[] { ("s1", badFrag) }));
    }

    // ── Assemble_OutputContainsNoUsingVar ─────────────────────────────────────

    /// <summary>
    /// The assembled output must not contain <c>using var</c> for standard
    /// fragments produced by <see cref="MakeFragment"/>.
    /// §13.3.1: <c>using var</c> is a parse error in a Roslyn script body.
    /// </summary>
    [Fact]
    public void Assemble_OutputContainsNoUsingVar()
    {
        var frag1 = MakeFragment("step-one", "StepOne_Helpers");
        var frag2 = MakeFragment("step-two", "StepTwo_Helpers");

        var assembled = CsxAssembler.Assemble(new (string, CsxFragment)[]
        {
            ("step-one", frag1),
            ("step-two", frag2),
        });

        Assert.DoesNotContain("using var", assembled.CsxSource, StringComparison.Ordinal);
    }

    // ── Assemble_CompilesAndRunsEndToEnd (B-04 proof) ─────────────────────────

    /// <summary>
    /// End-to-end B-04 proof: two <see cref="CsxFragment"/>s are assembled by
    /// <see cref="CsxAssembler.Assemble(IReadOnlyList{ValueTuple{string, CsxFragment}})"/>, compiled once by
    /// <see cref="RoslynScriptCompiler.CompileOnce"/>, and run through the
    /// collectible ALC via <see cref="RoslynScriptCompiler.RunIsolatedAsync"/>.
    /// Both steps' <c>Vars</c> keys must be written, confirming the multi-step
    /// assembled script runs through the delegate successfully.
    /// </summary>
    [Fact]
    public async Task Assemble_CompilesAndRunsEndToEnd()
    {
        // ── Arrange ───────────────────────────────────────────────────────────
        var frag1 = MakeFragment("step-alpha", "Alpha_Helpers");
        var frag2 = MakeFragment("step-beta", "Beta_Helpers");

        // ── Act: assemble ─────────────────────────────────────────────────────
        var assembled = CsxAssembler.Assemble(new (string, CsxFragment)[]
        {
            ("step-alpha", frag1),
            ("step-beta", frag2),
        });

        Assert.Equal(2, assembled.StepIds.Count);
        Assert.Equal("step-alpha", assembled.StepIds[0]);
        Assert.Equal("step-beta", assembled.StepIds[1]);

        // Guard: no "using var" in the assembled source.
        Assert.DoesNotContain("using var", assembled.CsxSource, StringComparison.Ordinal);

        // ── Act: compile once + run ───────────────────────────────────────────
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);
        Assert.NotEmpty(compiled.Image);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // ── Assert ────────────────────────────────────────────────────────────
        // MakeFragment writes Vars["step_<sanitised-id>"] = 1.
        // "step-alpha" → "step_alpha", "step-beta" → "step_beta".
        Assert.True(globals.Vars.ContainsKey("step_step_alpha"),
            $"Expected key 'step_step_alpha'. Actual keys: [{string.Join(", ", globals.Vars.Keys)}]");
        Assert.True(globals.Vars.ContainsKey("step_step_beta"),
            $"Expected key 'step_step_beta'. Actual keys: [{string.Join(", ", globals.Vars.Keys)}]");

        Assert.Equal(1, globals.Vars["step_step_alpha"]);
        Assert.Equal(1, globals.Vars["step_step_beta"]);
    }

    // ── CompileScenario_ReturnsStepIdsAndRunnableImage ────────────────────────

    /// <summary>
    /// <see cref="RoslynScriptCompiler.CompileScenario"/> must return a
    /// <see cref="CompiledScenario"/> whose <see cref="CompiledScenario.StepIds"/>
    /// matches the assembled input and whose <see cref="CompiledScenario.Compiled"/>
    /// image is executable via <see cref="RoslynScriptCompiler.RunIsolatedAsync"/>.
    /// </summary>
    [Fact]
    public async Task CompileScenario_ReturnsStepIdsAndRunnableImage()
    {
        var frag = MakeFragment("gamma", "Gamma_Helpers");

        var assembled = CsxAssembler.Assemble(new (string, CsxFragment)[] { ("gamma", frag) });

        // ── Act ───────────────────────────────────────────────────────────────
        var scenario = RoslynScriptCompiler.CompileScenario(assembled);

        // ── Assert: StepIds round-trip ────────────────────────────────────────
        Assert.Equal(assembled.StepIds, scenario.StepIds);

        // ── Assert: image is runnable ─────────────────────────────────────────
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(scenario.Compiled, globals);

        Assert.True(globals.Vars.ContainsKey("step_gamma"),
            $"Expected key 'step_gamma'. Actual keys: [{string.Join(", ", globals.Vars.Keys)}]");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
