// End-to-end lifecycle test for S02-F-01: reference provider drives the full
// resolve→bind→validate→plan→emit→compile→run pipeline against the real Roslyn
// memory-model compiler.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk.Tests.Providers;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Sdk.Tests;

public sealed class ReferenceProviderEndToEndTests
{
    /// <summary>
    /// Drives the complete provider lifecycle — bind → validate → emit → splice CSX
    /// → CompileOnce → RunIsolatedAsync — and asserts that the emitted script writes
    /// the expected value to <see cref="ScriptGlobalVariables.Vars"/>.
    /// </summary>
    [Fact]
    public async Task FullLifecycle_EmittedScript_WritesExpectedValueToVars()
    {
        // ── Arrange ───────────────────────────────────────────────────────────
        var provider = new NoopEchoProvider();

        // 1. Bind: construct a YAML mapping node mirroring the .e2e.yaml DSL shape.
        var yamlNode = new YamlMappingNode
        {
            { "message", new YamlScalarNode("hello") }
        };
        var model = provider.Bind(yamlNode, NullBindingContext.Instance);

        Assert.Equal("hello", model.Message);

        // 2. Validate: confirm the bound model is accepted.
        var validation = provider.Validate(model, NullProjectContext.Instance);
        Assert.True(validation.IsValid, $"Validation failed: {string.Join("; ", validation.Errors)}");

        // 3. Emit: produce the CsxFragment with a step-id that contains a hyphen
        //    so that id sanitisation is also exercised.
        var ctx = new CompileContextWithStepId("orders-db");
        var frag = provider.Emit(model, ctx);

        // 4. Splice the fragment into a complete CSX source string.
        //    Order: using directives → helper class definitions → statement block.
        //    Roslyn scripting allows top-level type declarations to precede top-level
        //    statements in a single submission when they appear before the statements.
        var usings = string.Join("\n", frag.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", frag.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{frag.StatementBlock}";

        // Guard: the spliced source must not contain "using var" (§13.3.1 invariant).
        Assert.DoesNotContain("using var", csx, StringComparison.Ordinal);

        // ── Act ───────────────────────────────────────────────────────────────

        // 5. Compile the CSX exactly once (the heart of the memory model).
        var compiled = RoslynScriptCompiler.CompileOnce(csx);

        // 6. Execute in an isolated, collectible AssemblyLoadContext.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // ── Assert ────────────────────────────────────────────────────────────

        // The sanitised step-id "orders-db" → "orders_db"; the script writes
        // Vars["echo_orders_db"] = NoopEcho_Helpers.Echo("hello".Length) = 5.
        const string expectedKey = "echo_orders_db";
        Assert.True(globals.Vars.ContainsKey(expectedKey),
            $"Expected Vars to contain key '{expectedKey}'. " +
            $"Actual keys: [{string.Join(", ", globals.Vars.Keys)}]");

        var rawValue = globals.Vars[expectedKey];
        Assert.NotNull(rawValue);

        // The script writes a boxed int; unbox directly to preserve type fidelity.
        Assert.Equal("hello".Length, (int)rawValue);
    }
}
