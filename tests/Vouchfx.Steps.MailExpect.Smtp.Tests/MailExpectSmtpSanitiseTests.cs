// Tests for MailExpectSmtpProvider — step-id sanitisation in emitted CSX.
//
// Covers (moved here from MailExpectSmtpSubstituteTests, which now exercises the
// {placeholder} + ${secret} substitution wiring):
//   1. A hyphenated step id emits the sanitised form in the outcome key and does NOT
//      emit the raw hyphenated form (it would be an invalid C# identifier).
//   2. An already-clean (no hyphens) step id passes through unchanged.
using Vouchfx.Sdk;
using Vouchfx.Steps.MailExpect.Smtp;
using Xunit;

namespace Vouchfx.Steps.MailExpect.Smtp.Tests;

/// <summary>
/// Verifies that <see cref="MailExpectSmtpProvider.Emit"/> sanitises the step id
/// before splicing it into variable names in the <see cref="CsxFragment.StatementBlock"/>.
/// </summary>
public sealed class MailExpectSmtpSanitiseTests
{
    private readonly MailExpectSmtpProvider _provider = new();

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public System.Collections.Generic.IReadOnlyDictionary<string, string> Captures { get; } =
            new System.Collections.Generic.Dictionary<string, string>();
        public System.Collections.Generic.IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new System.Collections.Generic.Dictionary<string, CaptureExpr>();
    }

    // ── 1. Hyphenated step id → sanitised outcome key ────────────────────────────

    [Fact]
    public void Emit_HyphenatedId_OutcomeKeyUsesSanitisedForm()
    {
        const string rawId = "expect-welcome-mail";
        var model = new MailExpectSmtpModel(
            Target: "mp",
            Expect: new MailExpectation(Match: new MailMatch(To: "a@b.com")));

        var fragment = _provider.Emit(model, new StubCtx(rawId));

        // The sanitised form (hyphens → underscores) must appear in the outcome key.
        Assert.Contains("__outcome::expect_welcome_mail", fragment.StatementBlock, StringComparison.Ordinal);
        // The raw hyphenated form must NOT appear as a variable-name fragment.
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 2. Clean step id → unchanged outcome key ─────────────────────────────────

    [Fact]
    public void Emit_CleanId_OutcomeKeyIsUnchanged()
    {
        const string cleanId = "expectMail";
        var model = new MailExpectSmtpModel(
            Target: "mp",
            Expect: new MailExpectation(Match: new MailMatch(SubjectContains: "hi")));

        var fragment = _provider.Emit(model, new StubCtx(cleanId));

        Assert.Contains("__outcome::expectMail", fragment.StatementBlock, StringComparison.Ordinal);
    }
}
