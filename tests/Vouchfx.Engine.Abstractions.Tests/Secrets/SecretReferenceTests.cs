// Tests for S05-B-01: SecretReference — parsing and pre-compile validation of
// ${secret:source/path} references (§17).
//
// Written RED-first before the implementation exists. These tests prove:
//   • A single well-formed token parses to (Source, Path) with Raw preserved.
//   • Paths may contain slashes and punctuation (vault kv layouts).
//   • Non-secret tokens (bare {placeholder}, plain text) do NOT parse — the
//     secret grammar must not collide with the {placeholder} grammar (B-03).
//   • FindAll extracts every token from a mixed literal+token field value.
//   • ValidateField rejects malformed sigils and unknown sources with an
//     actionable British-English message, and accepts well-formed known-source
//     tokens and plain literals.

using System.Collections.Generic;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies the <see cref="SecretReference"/> parsing and validation contracts
/// that underpin the engine's pre-compile secret-reference check (§17).
/// </summary>
public sealed class SecretReferenceTests
{
    // The single known source for this sprint (Vault arrives in Sprint 8).
    private static readonly string[] KnownSources = { "env" };

    // -------------------------------------------------------------------------
    // TryParse
    // -------------------------------------------------------------------------

    [Fact]
    public void TryParse_WellFormedEnvToken_ReturnsSourceAndPath()
    {
        const string token = "${secret:env/API_KEY}";

        var parsed = SecretReference.TryParse(token, out var reference);

        Assert.True(parsed);
        Assert.NotNull(reference);
        Assert.Equal("env", reference!.Source);
        Assert.Equal("API_KEY", reference.Path);
        Assert.Equal(token, reference.Raw);
    }

    [Fact]
    public void TryParse_PathWithSlashesAndPunctuation_KeepsFullPath()
    {
        const string token = "${secret:vault/kv/data/db}";

        var parsed = SecretReference.TryParse(token, out var reference);

        Assert.True(parsed);
        Assert.NotNull(reference);
        Assert.Equal("vault", reference!.Source);
        Assert.Equal("kv/data/db", reference.Path);
        Assert.Equal(token, reference.Raw);
    }

    [Theory]
    [InlineData("{plain}")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("${secret:env}")]              // missing /path → not a single valid token
    [InlineData("prefix ${secret:env/A}")]     // not EXACTLY one token (has literal prefix)
    public void TryParse_NotASecretToken_ReturnsFalse(string token)
    {
        var parsed = SecretReference.TryParse(token, out var reference);

        Assert.False(parsed);
        Assert.Null(reference);
    }

    // -------------------------------------------------------------------------
    // FindAll
    // -------------------------------------------------------------------------

    [Fact]
    public void FindAll_MultipleTokensInOneField_ReturnsAll()
    {
        const string field = "a ${secret:env/A} b ${secret:env/B}";

        var found = SecretReference.FindAll(field);

        Assert.Equal(2, found.Count);
        Assert.Equal("A", found[0].Path);
        Assert.Equal("B", found[1].Path);
        Assert.Equal("${secret:env/A}", found[0].Raw);
        Assert.Equal("${secret:env/B}", found[1].Raw);
    }

    [Fact]
    public void FindAll_BarePlaceholder_NoMatch()
    {
        // Proves no collision with the {placeholder} substitution grammar (B-03).
        const string field = "Bearer {token}";

        var found = SecretReference.FindAll(field);

        Assert.Empty(found);
    }

    [Fact]
    public void FindAll_PlainLiteral_NoMatch()
    {
        var found = SecretReference.FindAll("Bearer abc123");

        Assert.Empty(found);
    }

    // -------------------------------------------------------------------------
    // ValidateField
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateField_MalformedSigil_ReturnsError()
    {
        // Contains the literal sigil but no /path → malformed.
        var ok = SecretReference.ValidateField("${secret:env}", KnownSources, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ── ValidateSecretBearingField: the withholding sibling (#387 follow-up) ─────────────────
    //
    // A field that may hold a SECRET VALUE rather than only a reference to one
    // (security.clientKeyPassword) must not have its text interpolated into any diagnostic except
    // the unknown-source one, where the text is provably a whole reference — a pointer, never a
    // secret (§17). It is a SIBLING METHOD rather than a flag on ValidateField because the two
    // rules it applies must be atomic and in order: a caller composing them can get the order
    // wrong, and the wrong order discloses the value.

    /// <summary>
    /// <see cref="SecretReference.ValidateField"/> IS UNCHANGED, asserted byte for byte rather
    /// than assumed: every pre-existing caller is a step surface, whose field text is
    /// author-written template text and must keep being quoted, because that is what makes the
    /// diagnostic actionable.
    /// </summary>
    [Fact]
    public void ValidateField_MalformedSigil_StillQuotesTheFieldVerbatim()
    {
        const string field = "${secret:env}TRAILING-MARKER";

        var ok = SecretReference.ValidateField(field, KnownSources, out var error);

        Assert.False(ok);
        Assert.Equal(
            "the field '${secret:env}TRAILING-MARKER' contains a malformed secret reference; "
            + "the expected form is '${secret:<source>/<path>}' (for example "
            + "'${secret:env/API_TOKEN}').",
            error);
    }

    [Fact]
    public void ValidateSecretBearingField_MalformedSigil_WithholdsTheFieldValue()
    {
        const string field = "${secret:env}TRAILING-MARKER";

        var ok = SecretReference.ValidateSecretBearingField(field, KnownSources, out var error);

        Assert.False(ok);
        Assert.Equal(SecretReference.WithheldValueMessage, error);
        Assert.DoesNotContain("TRAILING-MARKER", error!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A PLAIN LITERAL — no sigil at all — is accepted by <see cref="SecretReference.ValidateField"/>
    /// by design (a step's field may be ordinary text) and REFUSED by
    /// <see cref="SecretReference.ValidateSecretBearingField"/>. This is the divergence that makes
    /// the whole-token rule load-bearing: for a secret-bearing field, a literal IS the plaintext
    /// secret.
    /// </summary>
    [Fact]
    public void ValidateSecretBearingField_PlainLiteral_IsRefused_WhereValidateFieldAcceptsIt()
    {
        const string field = "hunter2-PLAINTEXT-MARKER";

        Assert.True(SecretReference.ValidateField(field, KnownSources, out var permitted));
        Assert.Null(permitted);

        Assert.False(SecretReference.ValidateSecretBearingField(field, KnownSources, out var refused));
        Assert.Equal(SecretReference.WithheldValueMessage, refused);
        Assert.DoesNotContain("PLAINTEXT-MARKER", refused!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The nested-sigil shape reaches the malformed branch even though
    /// <see cref="SecretReference.TryParse"/> accepts it — the two disagree, which is exactly why
    /// both rules have to be applied and why a caller cannot predict which fires.
    /// </summary>
    [Fact]
    public void ValidateSecretBearingField_NestedSigil_TryParseAcceptsItButTheValueIsStillWithheld()
    {
        const string field = "${secret:env/PASS${secret:INNER-MARKER}";

        Assert.True(SecretReference.TryParse(field, out _));

        var quoted = SecretReference.ValidateField(field, KnownSources, out var defaultError);
        Assert.False(quoted);
        Assert.Contains("INNER-MARKER", defaultError!, System.StringComparison.Ordinal);

        var withheld = SecretReference.ValidateSecretBearingField(field, KnownSources, out var secretError);
        Assert.False(withheld);
        Assert.DoesNotContain("INNER-MARKER", secretError!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The adversarial shape: an unknown source AND a nested sigil. The source check alone would
    /// route this to the quoting branch; the sigil arithmetic routes it to the withholding one.
    /// </summary>
    [Fact]
    public void ValidateSecretBearingField_UnknownSourceAndNestedSigil_StillWithholds()
    {
        const string field = "${secret:nosuchsource/PASS${secret:INNER-MARKER}";

        Assert.True(SecretReference.TryParse(field, out var parsed));
        Assert.Equal("nosuchsource", parsed!.Source);

        Assert.False(SecretReference.ValidateSecretBearingField(field, KnownSources, out var error));
        Assert.Equal(SecretReference.WithheldValueMessage, error);
        Assert.DoesNotContain("INNER-MARKER", error!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The UNKNOWN-SOURCE message is identical across the two methods: it quotes
    /// <c>match.Value</c>, a whole well-formed reference, and §17 is explicit that a reference is
    /// a pointer and never a secret. EDGE-003 requires this message to stay byte-identical across
    /// the step and security surfaces, so the two are compared to each other directly rather than
    /// each to a hand-copied literal.
    /// </summary>
    [Fact]
    public void UnknownSource_MessageIsIdenticalAcrossBothMethods()
    {
        const string field = "${secret:nosuchsource/TOKEN}";

        var a = SecretReference.ValidateField(field, KnownSources, out var stepError);
        var b = SecretReference.ValidateSecretBearingField(field, KnownSources, out var securityError);

        Assert.False(a);
        Assert.False(b);
        Assert.Equal(stepError, securityError);
        Assert.Contains("names an unknown source 'nosuchsource'", stepError!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The happy path: one whole reference naming a known source passes both methods.
    /// </summary>
    [Fact]
    public void ValidateSecretBearingField_WholeReferenceWithKnownSource_IsAccepted()
    {
        Assert.True(SecretReference.ValidateSecretBearingField(
            "${secret:env/CLIENT_KEY_PASS}", KnownSources, out var error));
        Assert.Null(error);
    }

    /// <summary>
    /// The EMPTY-value divergence, pinned rather than left to be rediscovered: permitted by
    /// <see cref="SecretReference.ValidateField"/> (a step's field may be empty text carrying no
    /// reference) and refused by <see cref="SecretReference.ValidateSecretBearingField"/> (an
    /// empty passphrase declaration resolves to nothing and cannot be honoured).
    /// </summary>
    [Fact]
    public void EmptyValue_IsPermittedByValidateField_AndRefusedForASecretBearingField()
    {
        Assert.True(SecretReference.ValidateField(string.Empty, KnownSources, out var permitted));
        Assert.Null(permitted);

        Assert.False(SecretReference.ValidateSecretBearingField(string.Empty, KnownSources, out var refused));
        Assert.Equal(SecretReference.WithheldValueMessage, refused);
    }

    [Fact]
    public void ValidateField_UnknownSource_ReturnsError()
    {
        var ok = SecretReference.ValidateField(
            "${secret:vault/x}",
            KnownSources,
            out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        // The message must name the offending source so the author can act on it.
        Assert.Contains("vault", error!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateField_WellFormedKnownSource_NoError()
    {
        var ok = SecretReference.ValidateField(
            "Bearer ${secret:env/API}",
            KnownSources,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateField_PlainLiteral_NoError()
    {
        // Plain literals are allowed — secret-shaped-literal linting is a FUTURE rule.
        var ok = SecretReference.ValidateField("Bearer abc123", KnownSources, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateField_MultipleTokensOneUnknown_ReturnsError()
    {
        // First token is fine; the second uses an unknown source → overall invalid.
        var ok = SecretReference.ValidateField(
            "${secret:env/A} ${secret:vault/B}",
            KnownSources,
            out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("vault", error!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateField_NoSigil_EmptyKnownSources_NoError()
    {
        // A field with no secret sigil is always valid regardless of knownSources.
        var ok = SecretReference.ValidateField(
            "plain text {placeholder}",
            new List<string>(),
            out var error);

        Assert.True(ok);
        Assert.Null(error);
    }
}
