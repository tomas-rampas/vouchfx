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
using Platform.Engine.Abstractions.Secrets;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

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
