// Tests for the NoopEcho reference provider — S02-F-01 lifecycle contract.
// Covers: identity/metadata, reflection discovery, five-stage cohesion, CsxFragment guards.
using System.Reflection;
using Platform.Sdk.Tests.Providers;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Sdk.Tests;

public sealed class ReferenceProviderTests
{
    // All five roles are exercised through the single consolidated provider instance.
    private readonly NoopEchoProvider _provider = new();

    // ── 1. Identity and metadata ──────────────────────────────────────────────

    [Fact]
    public void Provider_HasExpectedStepKindId()
    {
        var kind = _provider.Kind;
        Assert.Equal("noop", kind.Family);
        Assert.Equal("echo", kind.Provider);
    }

    [Fact]
    public void Provider_MetadataIsPopulated()
    {
        var meta = _provider.Metadata;
        Assert.False(string.IsNullOrWhiteSpace(meta.Version));
        Assert.False(string.IsNullOrWhiteSpace(meta.MinEngineVersion));
        Assert.False(string.IsNullOrWhiteSpace(meta.License));
        Assert.NotEmpty(meta.Authors);
    }

    [Fact]
    public void Provider_IsDecoratedWithStepProviderAttribute()
    {
        var attr = typeof(NoopEchoProvider)
            .GetCustomAttribute<StepProviderAttribute>();
        Assert.NotNull(attr);
    }

    // ── 2. Bind produces the model ────────────────────────────────────────────

    [Fact]
    public void Binder_BindsYamlNodeToModel()
    {
        var yaml = new YamlMappingNode
        {
            { "message", new YamlScalarNode("hello") }
        };
        var model = _provider.Bind(yaml, NullBindingContext.Instance);
        Assert.IsAssignableFrom<IStepModel>(model);
        Assert.Equal("hello", ((NoopEchoModel)model).Message);
    }

    [Fact]
    public void Binder_SchemaFragment_ContainsJson()
    {
        Assert.False(string.IsNullOrWhiteSpace(_provider.SchemaFragment.Json));
    }

    // ── 3. Validate returns correct verdicts ──────────────────────────────────

    [Fact]
    public void Validator_ReturnsSuccess_ForValidModel()
    {
        var result = _provider.Validate(new NoopEchoModel("hello"), NullProjectContext.Instance);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_ReturnsFailure_ForInvalidModel()
    {
        var result = _provider.Validate(new NoopEchoModel(string.Empty), NullProjectContext.Instance);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // ── 4. Resources returns the declared requirement ─────────────────────────

    [Fact]
    public void ResourceContributor_ReturnsExpectedRequirement()
    {
        var reqs = _provider.Resources(new NoopEchoModel("hello")).ToList();
        Assert.Single(reqs);
        var req = reqs[0];
        Assert.Equal("noop", req.Family);
        Assert.Equal("echo-service", req.Name);
    }

    // ── 5. Emit produces a well-formed CsxFragment ────────────────────────────

    [Fact]
    public void Compiler_Emit_StatementBlockStartsAndEndsWithBrace()
    {
        var frag = _provider.Emit(new NoopEchoModel("hello"), NullCompileContext.Instance);
        var trimmed = frag.StatementBlock.Trim();
        Assert.StartsWith("{", trimmed);
        Assert.EndsWith("}", trimmed);
    }

    [Fact]
    public void Compiler_Emit_StatementBlockContainsSanitisedId_NotHyphenatedId()
    {
        // The compiler is passed a step-id with a hyphen; the emitted block must
        // contain the underscore form and must NOT contain the hyphenated form.
        const string hyphenatedId = "orders-db";
        var ctx = new CompileContextWithStepId(hyphenatedId);
        var frag = _provider.Emit(new NoopEchoModel("hello"), ctx);

        var sanitised = CsxFragment.SanitiseId(hyphenatedId);
        Assert.Contains(sanitised, frag.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(hyphenatedId, frag.StatementBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_Emit_RequiredUsings_AreBarePrefixNamespaces()
    {
        var frag = _provider.Emit(new NoopEchoModel("hello"), NullCompileContext.Instance);
        foreach (var u in frag.RequiredUsings)
        {
            Assert.False(u.TrimStart().StartsWith("using ", StringComparison.Ordinal),
                $"RequiredUsings entry must be a bare namespace, not an inline 'using' directive. Got: {u}");
        }
    }

    [Fact]
    public void Compiler_Emit_RequiredHelpers_AreProviderIdPrefixed()
    {
        // RequiredHelpers entries are full static class DEFINITIONS whose declared
        // class name is prefixed with the provider id (§13.3.1).
        var frag = _provider.Emit(new NoopEchoModel("hello"), NullCompileContext.Instance);
        foreach (var h in frag.RequiredHelpers)
        {
            Assert.True(h.Contains("NoopEcho_", StringComparison.Ordinal),
                $"RequiredHelpers entries must contain a provider-id-prefixed class name. Got: {h}");
            Assert.True(h.Contains("class NoopEcho_", StringComparison.Ordinal),
                $"RequiredHelpers entries must declare a 'class NoopEcho_' type. Got: {h}");
        }
    }

    // ── 6. CsxFragment guards (§13.3.1 invariants) ───────────────────────────

    [Fact]
    public void EmittedStatementBlock_ContainsNoInlineUsingDirective()
    {
        var frag = _provider.Emit(new NoopEchoModel("hello"), NullCompileContext.Instance);
        // "using " as a statement at the start of a line is illegal in a Roslyn script body.
        Assert.DoesNotContain("using ", frag.StatementBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void EmittedStatementBlock_ContainsNoUsingVar()
    {
        var frag = _provider.Emit(new NoopEchoModel("hello"), NullCompileContext.Instance);
        // "using var" causes a parse error in any Roslyn script-language version.
        Assert.DoesNotContain("using var", frag.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. ValidationResult.Success convenience ───────────────────────────────

    [Fact]
    public void ValidationResult_Success_IsValidAndNoErrors()
    {
        var ok = ValidationResult.Success;
        Assert.True(ok.IsValid);
        Assert.Empty(ok.Errors);
    }
}
