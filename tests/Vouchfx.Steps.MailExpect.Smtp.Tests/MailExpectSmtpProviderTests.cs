// Tests for MailExpectSmtpProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full YAML step (target + expect block with count, match.to,
//      match.subject-contains, match.body-contains) → correct model fields.
//   2. Bind: non-mapping node → safe empty model (defensive).
//   3. Bind: expect block missing match → empty MailMatch.
//   4. Validate: valid model (declared mailpit dep + at least one criterion) → IsValid.
//   5. Validate: empty target → invalid with message.
//   6. Validate: target not declared → invalid with message.
//   7. Validate: target declared as wrong type (postgres) → invalid with message.
//   8. Validate: all match criteria null → invalid with message.
//   9. Validate: count < 1 → invalid with message.
//  10. Registry: provider discoverable at key "mail-expect.smtp".
//  11. Registry: SchemaFragment references "expect".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MailExpect.Smtp;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MailExpect.Smtp.Tests;

// ── Stub context implementations ─────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> that exposes a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for validator unit tests.
/// </summary>
file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal StubProjectContext(IReadOnlyDictionary<string, string>? deps = null)
    {
        DeclaredDependencies = deps
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
}

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require
/// binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MailExpectSmtpProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MailExpectSmtpProviderTests
{
    private readonly MailExpectSmtpProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML mapping ─────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("mailpit") },
            {
                "expect", new YamlMappingNode
                {
                    { "count", new YamlScalarNode("2") },
                    {
                        "match", new YamlMappingNode
                        {
                            { "to",               new YamlScalarNode("alice@example.com") },
                            { "subject-contains", new YamlScalarNode("Welcome") },
                            { "body-contains",    new YamlScalarNode("Hello, Alice") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("mailpit", model.Target);
        Assert.Equal(2, model.Expect.Count);
        Assert.Equal("alice@example.com", model.Expect.Match.To);
        Assert.Equal("Welcome", model.Expect.Match.SubjectContains);
        Assert.Equal("Hello, Alice", model.Expect.Match.BodyContains);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Null(model.Expect.Count);
        Assert.Null(model.Expect.Match.To);
        Assert.Null(model.Expect.Match.SubjectContains);
        Assert.Null(model.Expect.Match.BodyContains);
    }

    // ── 3. Bind: expect without match → empty MailMatch ────────────────────────

    [Fact]
    public void Bind_ExpectWithoutMatch_YieldsEmptyMailMatch()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("mp") },
            { "expect", new YamlMappingNode { { "count", new YamlScalarNode("1") } } },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Expect.Match.To);
        Assert.Null(model.Expect.Match.SubjectContains);
        Assert.Null(model.Expect.Match.BodyContains);
        Assert.Equal(1, model.Expect.Count);
    }

    // ── 4. Validate: valid model ────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_ReturnsSuccess()
    {
        var model = MakeModel("mailpit", new MailMatch(To: "user@example.com"));
        var ctx = new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["mailpit"] = "mailpit" });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 5. Validate: empty target ───────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_ReturnsInvalid()
    {
        var model = MakeModel(string.Empty, new MailMatch(To: "x@y.com"));
        var ctx = new StubProjectContext();

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target", StringComparison.OrdinalIgnoreCase));
    }

    // ── 6. Validate: target not declared ────────────────────────────────────────

    [Fact]
    public void Validate_TargetNotDeclared_ReturnsInvalid()
    {
        var model = MakeModel("mailpit", new MailMatch(To: "x@y.com"));
        var ctx = new StubProjectContext();

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("mailpit", StringComparison.Ordinal) &&
            e.Contains("not declared", StringComparison.OrdinalIgnoreCase));
    }

    // ── 7. Validate: target declared as wrong type ───────────────────────────────

    [Fact]
    public void Validate_TargetWrongType_ReturnsInvalid()
    {
        var model = MakeModel("db", new MailMatch(To: "x@y.com"));
        var ctx = new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["db"] = "postgres" });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("postgres", StringComparison.Ordinal) &&
            e.Contains("mailpit", StringComparison.Ordinal));
    }

    /// <summary>
    /// Dependency type comparison is case-sensitive (pre-GA decision,
    /// feat/case-sensitive-kinds): "Mailpit" does not match the canonical "mailpit" — treated
    /// identically to a genuinely mismatched type.
    /// </summary>
    [Fact]
    public void Validate_TargetWrongCase_ReturnsInvalid()
    {
        var model = MakeModel("db", new MailMatch(To: "x@y.com"));
        var ctx = new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["db"] = "Mailpit" });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("db", StringComparison.Ordinal));
    }

    // ── 8. Validate: all match criteria null ─────────────────────────────────────

    [Fact]
    public void Validate_AllMatchCriteriaNullModel_ReturnsInvalid()
    {
        var model = MakeModel("mailpit", new MailMatch(To: null, SubjectContains: null, BodyContains: null));
        var ctx = new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["mailpit"] = "mailpit" });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("criterion", StringComparison.OrdinalIgnoreCase));
    }

    // ── 9. Validate: count < 1 ───────────────────────────────────────────────────

    [Fact]
    public void Validate_CountZero_ReturnsInvalid()
    {
        var model = new MailExpectSmtpModel(
            Target: "mailpit",
            Expect: new MailExpectation(
                Match: new MailMatch(To: "a@b.com"),
                Count: 0));
        var ctx = new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["mailpit"] = "mailpit" });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("count", StringComparison.OrdinalIgnoreCase));
    }

    // ── 10. Registry: step kind discoverable ─────────────────────────────────────

    [Fact]
    public void Registry_ProvidesStepKind_MailExpectSmtp()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MailExpectSmtpProvider).Assembly });

        Assert.True(
            registry.TryGet("mail-expect.smtp", out var provider) && provider is not null,
            "Expected 'mail-expect.smtp' to be registered.");
    }

    // ── 11. SchemaFragment references 'expect' ────────────────────────────────────

    [Fact]
    public void SchemaFragment_ContainsExpectField()
    {
        var json = _provider.SchemaFragment.Json;

        Assert.Contains("expect", json, StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private static MailExpectSmtpModel MakeModel(string target, MailMatch match)
        => new(
            Target: target,
            Expect: new MailExpectation(Match: match));
}
