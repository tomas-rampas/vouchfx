// Tests for S05-B-03: ReproducibilityEnvelope — the envelope hashes the secret
// REFERENCE (never the resolved value) and records fixture content hashes (§17,
// docs/02 §3.2.2).
//
// Written TEST-FIRST.  These tests prove the graded acceptance:
//   "The envelope contains a hash of the reference and provably no resolved secret
//    material."
//
// The decisive proof of secret-safety:
//   • An env var is set to a KNOWN value, then a digest / envelope is built for the
//     reference ${secret:env/<name>}.  Because the envelope is built from REFERENCE
//     TEXT only (the resolver is never called), the value must be absent from the
//     digest AND from the serialised envelope — even though the value is sitting in
//     the process environment, ready to leak if any code path read it.
//   • The reference hash equals SHA256hex(rawToken), so it is stable across runs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Reproducibility;

/// <summary>
/// Verifies the <see cref="ReproducibilityEnvelope"/> secret-safety and
/// reproducibility contracts (§17, docs/02 §3.2.2).
/// </summary>
public sealed class ReproducibilityEnvelopeTests
{
    private const string KnownSecretValue = "topsecret-value-must-never-appear-7f3a";

    /// <summary>
    /// Independently computes lower-case hex SHA-256 of a string's UTF-8 bytes,
    /// so the test does not merely echo the implementation's own helper.
    /// </summary>
    private static string Sha256Hex(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    // -------------------------------------------------------------------------
    // 1. The reference is hashed — not the value.
    // -------------------------------------------------------------------------

    /// <summary>
    /// With the backing env var set to a known value, a digest built for
    /// <c>${secret:env/&lt;name&gt;}</c> must hash the verbatim TOKEN
    /// (<c>SHA256hex(rawToken)</c>), and neither the digest nor the serialised
    /// envelope may contain the resolved value (§17).
    /// </summary>
    [Fact]
    public void HashReference_HashesRawToken_NotValue()
    {
        var envName = "VOUCHFX_REPRO_HASH_" + Guid.NewGuid().ToString("N");
        var rawToken = $"${{secret:env/{envName}}}";

        // Set the env var to a known value so a careless value-read WOULD leak it.
        Environment.SetEnvironmentVariable(envName, KnownSecretValue);
        try
        {
            Assert.True(SecretReference.TryParse(rawToken, out var reference));
            Assert.NotNull(reference);

            var digest = new SecretReferenceDigest(
                Source: reference!.Source,
                ReferenceHash: ReproducibilityEnvelope.HashReference(reference.Raw));

            // The hash is over the RAW token, byte-for-byte.
            Assert.Equal(Sha256Hex(rawToken), digest.ReferenceHash);
            Assert.Equal("env", digest.Source);

            // The value must appear NOWHERE on the digest.
            Assert.DoesNotContain(KnownSecretValue, digest.ReferenceHash, StringComparison.Ordinal);
            Assert.DoesNotContain(KnownSecretValue, digest.Source, StringComparison.Ordinal);

            // Build a full envelope and serialise it: the value must be absent.
            var envelope = ReproducibilityEnvelope.Compute(
                new[] { reference }, Array.Empty<FixtureDigest>());

            var json = JsonSerializer.Serialize(envelope, EventStreamJson.Options);
            Assert.DoesNotContain(KnownSecretValue, json, StringComparison.Ordinal);

            // Positive: the reference hash IS present (the headline acceptance).
            Assert.Contains(Sha256Hex(rawToken), json, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // -------------------------------------------------------------------------
    // 2. Reproducibility: same reference → same hash.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The same reference token always hashes to the same digest, regardless of
    /// run — this is the reproducibility property (§17).
    /// </summary>
    [Fact]
    public void SameReference_SameHash()
    {
        const string raw = "${secret:env/API_TOKEN}";

        var first = ReproducibilityEnvelope.HashReference(raw);
        var second = ReproducibilityEnvelope.HashReference(raw);

        Assert.Equal(first, second);
        Assert.Equal(Sha256Hex(raw), first);
    }

    // -------------------------------------------------------------------------
    // 3. Uniqueness: a different path → a different hash.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Two references that differ only in their path must produce different
    /// digests, so the envelope distinguishes distinct inputs.
    /// </summary>
    [Fact]
    public void DifferentPath_DifferentHash()
    {
        var a = ReproducibilityEnvelope.HashReference("${secret:env/API_TOKEN}");
        var b = ReproducibilityEnvelope.HashReference("${secret:env/DB_PASSWORD}");

        Assert.NotEqual(a, b);
    }

    // -------------------------------------------------------------------------
    // 4. Compute dedupes references by Raw.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The same reference appearing multiple times (e.g. in two fields) is one
    /// input and must contribute exactly one digest, preserving first-seen order.
    /// </summary>
    [Fact]
    public void Compute_DedupesReferencesByRaw()
    {
        const string rawA = "${secret:env/A}";
        const string rawB = "${secret:env/B}";

        Assert.True(SecretReference.TryParse(rawA, out var refA1));
        Assert.True(SecretReference.TryParse(rawA, out var refA2));
        Assert.True(SecretReference.TryParse(rawB, out var refB));

        var envelope = ReproducibilityEnvelope.Compute(
            new[] { refA1!, refB!, refA2! },
            Array.Empty<FixtureDigest>());

        Assert.Equal(2, envelope.SecretReferences.Count);
        // First-seen order preserved: A then B.
        Assert.Equal(Sha256Hex(rawA), envelope.SecretReferences[0].ReferenceHash);
        Assert.Equal(Sha256Hex(rawB), envelope.SecretReferences[1].ReferenceHash);
    }

    // -------------------------------------------------------------------------
    // 5. Fixtures are carried through verbatim.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The supplied fixture digests are carried into the envelope unchanged and the
    /// schema version is the current one.
    /// </summary>
    [Fact]
    public void Compute_CarriesFixturesAndSchemaVersion()
    {
        var fixtures = new List<FixtureDigest>
        {
            new("fixtures/orders.sql", "aabbcc"),
            // The COULD-NOT-BE-HASHED shape — a locked file, a permission denial or a rejected
            // path as much as an absent one, with no cause recorded. Serialises as a row with
            // no contentHash KEY at all (WhenWritingNull), never an explicit null.
            new("fixtures/catalog.json", null),
        };

        var envelope = ReproducibilityEnvelope.Compute(
            Array.Empty<SecretReference>(), fixtures);

        Assert.Equal(ReproducibilityEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal("v1", envelope.SchemaVersion);
        Assert.Equal(2, envelope.Fixtures.Count);
        Assert.Equal("fixtures/orders.sql", envelope.Fixtures[0].Reference);
        Assert.Equal("aabbcc", envelope.Fixtures[0].ContentHash);
        Assert.Null(envelope.Fixtures[1].ContentHash);
    }

    // -------------------------------------------------------------------------
    // 6. Serialised envelope contains no secret value (defence in depth).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialising the envelope via both <see cref="JsonSerializer"/> and
    /// <see cref="EventStreamJson.ToLine{T}"/> (the path the runner uses to wrap it
    /// in an event) must never surface the resolved secret value (§17).
    /// </summary>
    [Fact]
    public void Serialised_Envelope_ContainsNoSecretValue()
    {
        var envName = "VOUCHFX_REPRO_SER_" + Guid.NewGuid().ToString("N");
        var rawToken = $"${{secret:env/{envName}}}";

        Environment.SetEnvironmentVariable(envName, KnownSecretValue);
        try
        {
            Assert.True(SecretReference.TryParse(rawToken, out var reference));

            var envelope = ReproducibilityEnvelope.Compute(
                new[] { reference! },
                new[] { new FixtureDigest("fixtures/seed.sql", "deadbeef") });

            // Direct serialisation.
            var directJson = JsonSerializer.Serialize(envelope, EventStreamJson.Options);
            Assert.DoesNotContain(KnownSecretValue, directJson, StringComparison.Ordinal);

            // Wrapped in the event the runner emits, via the shared ToLine helper.
            var line = EventStreamJson.ToLine(new ReproducibilityEnvelopeEvent
            {
                RunId = "run-repro-test",
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = "repro-scenario",
                EnvSchemaVersion = envelope.SchemaVersion,
                SecretReferences = envelope.SecretReferences,
                Fixtures = envelope.Fixtures,
            });

            Assert.DoesNotContain(KnownSecretValue, line, StringComparison.Ordinal);

            // Round-trips and still carries the reference hash + fixture hash.
            var back = EventStreamJson.FromLine<ReproducibilityEnvelopeEvent>(line);
            Assert.Equal(EventTypes.ReproducibilityEnvelope, back.Type);
            Assert.Single(back.SecretReferences);
            Assert.Equal(Sha256Hex(rawToken), back.SecretReferences[0].ReferenceHash);
            Assert.Equal("env", back.SecretReferences[0].Source);
            Assert.Single(back.Fixtures);
            Assert.Equal("deadbeef", back.Fixtures[0].ContentHash);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // -------------------------------------------------------------------------
    // 7. HashReference rejects null.
    // -------------------------------------------------------------------------

    [Fact]
    public void HashReference_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ReproducibilityEnvelope.HashReference(null!));
    }
}
