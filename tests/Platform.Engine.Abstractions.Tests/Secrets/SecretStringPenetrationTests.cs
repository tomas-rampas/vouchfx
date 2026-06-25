// S11-B-01 — secret-redaction penetration suite, CARRIER layer (§17).
//
// This file is the adversarial counterpart to SecretStringTests: where that file
// pins the happy-path contract (ToString → marker, no IFormattable, JSON converter
// redacts), this file enumerates the cases a real attacker — or a careless author —
// actually reaches and proves, case by case, that the engine's OWN handling of a
// SecretString never lets the value out.
//
// Each test is tagged in its summary as one of:
//   (a) GENUINE-LEAK-FIXED  — the engine had (or could have) a real escape; the test
//       was red before a fix and is green after, fixing the engine path.
//   (b) OUT-OF-SCOPE-BY-DESIGN — the only way the value escapes is a deliberate
//       Reveal() call (the documented, greppable, auditable escape hatch §17) followed
//       by the AUTHOR transforming the revealed value.  The test asserts the engine's
//       OWN path (ToString / interpolation / serialisation / Convert.ToString) stays
//       clean, and a comment explains why the deliberate-reveal variant is the author's
//       responsibility, not an engine defect.
//
// All cases at THIS layer turn out to be (b): the carrier is the type-based redaction
// boundary, and by construction it never derives, encodes, signs, or substrings its
// own value — only a Reveal() caller can.  The GENUINE engine-side leak this task
// found and fixed lives one layer up (a script.csharp exception message reaching the
// event stream); it is covered by the runtime-layer penetration suite
// (SecretObservationLeakPenetrationTests).  Keeping the carrier cases here documents,
// permanently, exactly where the trust boundary sits.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Engine.Abstractions.Secrets;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Adversarial penetration tests over the <see cref="SecretString"/> carrier (§17):
/// base64, HMAC/SigV4-derived, partial/substring, and concatenation attack shapes.
/// Each test pins that the engine's OWN redaction path stays clean and documents why a
/// deliberate-<see cref="SecretString.Reveal"/> variant is out of scope by design.
/// </summary>
public sealed class SecretStringPenetrationTests
{
    // A high-entropy stand-in for a real credential.  If any engine path stringifies
    // the carrier to its value, the assertions below catch this literal.
    private const string TheValue = "AKIA-pentest-secret-7f3c91e0d2b4";

    // ── base64-encoded secret reaching an output sink ────────────────────────────

    /// <summary>
    /// (b) OUT-OF-SCOPE-BY-DESIGN.  A base64 ENCODING of the secret only exists if the
    /// value was first revealed and then transformed by author code — the engine never
    /// base64-encodes a <see cref="SecretString"/>.  This test proves the engine's own
    /// stringification paths (ToString, interpolation, JSON) emit NEITHER the raw value
    /// NOR its base64 form, so an accidental "log the secret" never produces a base64
    /// leak through any engine surface.
    /// </summary>
    /// <remarks>
    /// The deliberate variant — <c>Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Reveal()))</c>
    /// then writing THAT to a sink — is the author transforming a revealed value; that is
    /// the documented escape hatch (§17), not an engine defect.  We still assert the
    /// base64 form is absent from every engine-mediated representation so a base64 leak
    /// can never originate inside the engine.
    /// </remarks>
    [Fact]
    public void Base64_EngineRepresentationsContainNeitherValueNorEncoding()
    {
        var secret = new SecretString(TheValue);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(TheValue));

        var viaToString = secret.ToString();
        var viaInterpolation = $"Authorization: Basic {secret}";
        var viaJson = JsonSerializer.Serialize(new { token = secret });

        foreach (var representation in new[] { viaToString, viaInterpolation, viaJson })
        {
            Assert.DoesNotContain(TheValue, representation, StringComparison.Ordinal);
            Assert.DoesNotContain(base64, representation, StringComparison.Ordinal);
        }

        // Every engine-mediated representation still carries the redaction marker.
        Assert.Contains(SecretString.RedactedMarker, viaInterpolation, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, viaJson, StringComparison.Ordinal);
    }

    // ── HMAC / AWS SigV4-signed derived form (secret used as a signing key) ───────

    /// <summary>
    /// (b) OUT-OF-SCOPE-BY-DESIGN.  Using the secret as an HMAC signing key (the core of
    /// AWS SigV4) produces a DERIVED signature, not the value.  The engine never signs
    /// with a <see cref="SecretString"/> — only author code that calls
    /// <see cref="SecretString.Reveal"/> to obtain the key bytes can.  This test proves:
    /// (1) the engine's own carrier surfaces never expose the key, and (2) a SigV4-style
    /// signature is a one-way function — the signature does not contain the key, so
    /// emitting the SIGNATURE (the normal, safe SigV4 pattern) is not itself a key leak.
    /// </summary>
    /// <remarks>
    /// The irreducible author responsibility: do not log <c>Reveal()</c> output and do not
    /// use a guessable key.  The engine cannot and should not intercept the HMAC of a
    /// revealed value — that is legitimate signing at an injection sink (§17).
    /// </remarks>
    [Fact]
    public void HmacSigV4_SignatureIsOneWay_AndCarrierNeverExposesKey()
    {
        var secret = new SecretString(TheValue);

        // SigV4 derives the signing key by chained HMAC; the simplest probe is a single
        // HMAC of a canonical request using the revealed value as the key.  Reveal() is
        // the deliberate escape hatch — exactly what a signing sink legitimately uses.
        var keyBytes = Encoding.UTF8.GetBytes(secret.Reveal());
        using var hmac = new HMACSHA256(keyBytes);
        var signature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes("GET\n/\n\nhost:example.com\n")));

        // The derived signature is one-way: it does not contain the key value.  So an
        // author emitting the SIGNATURE (the safe SigV4 pattern) leaks nothing.
        Assert.DoesNotContain(TheValue, signature, StringComparison.OrdinalIgnoreCase);

        // The engine's own carrier surfaces never expose the signing key.
        Assert.DoesNotContain(TheValue, secret.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TheValue, $"X-Amz-Signature={secret}", StringComparison.Ordinal);
    }

    // ── partial logging (a substring / prefix of the secret) ─────────────────────

    /// <summary>
    /// (b) OUT-OF-SCOPE-BY-DESIGN.  A partial/prefix leak ("token starts with AKIA…")
    /// can only arise if author code reveals the value and then substrings it.  The
    /// engine never substrings a <see cref="SecretString"/>; crucially it does not even
    /// expose the LENGTH publicly (an oracle channel), and ToString is the whole-marker,
    /// not a masked-with-prefix form.  This test pins that the engine emits no prefix of
    /// any length and exposes no public length.
    /// </summary>
    [Fact]
    public void PartialLogging_EngineEmitsNoPrefix_AndNoPublicLength()
    {
        var secret = new SecretString(TheValue);
        var rendered = secret.ToString();

        // No non-empty prefix of the secret appears in the engine's stringification.
        // (Start at length 4 — shorter prefixes risk coincidental matches in marker text,
        // and a 4+ char high-entropy prefix is already an oracle.)
        for (var len = 4; len <= TheValue.Length; len++)
        {
            var prefix = TheValue[..len];
            Assert.DoesNotContain(prefix, rendered, StringComparison.Ordinal);
        }

        // The exact length is itself an oracle (§17): the engine must not expose it
        // publicly.  Length is internal-only — assert there is no public Length member.
        var publicLength = typeof(SecretString).GetProperty(
            "Length",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Null(publicLength);
    }

    // ── concatenation of a SecretString into a larger string before a sink ───────

    /// <summary>
    /// (a→b) The classic accident: concatenating a secret into a larger string with
    /// <c>+</c> or string interpolation BEFORE it hits a sink.  Because
    /// <see cref="SecretString"/> is deliberately NOT <see cref="IFormattable"/> and has
    /// NO implicit string conversion, both <c>"Bearer " + secret</c> and
    /// <c>$"Bearer {secret}"</c> route through <see cref="object.ToString"/> and embed the
    /// REDACTION MARKER, never the value.  This is the engine's structural defence and is
    /// the reason the accident is contained rather than catastrophic — so this case is
    /// genuinely DEFENDED by the engine (it would be a real leak under a record/implicit
    /// conversion), and the test pins that defence permanently.
    /// </summary>
    [Fact]
    public void Concatenation_IntoLargerString_EmbedsMarkerNotValue()
    {
        var secret = new SecretString(TheValue);

        var viaPlus = "Bearer " + secret;
        var viaInterp = $"Bearer {secret}";
        var viaConcat = string.Concat("Authorization: ", secret, "; trailing");
        var viaBuilder = new StringBuilder().Append("hdr=").Append(secret).ToString();

        foreach (var s in new[] { viaPlus, viaInterp, viaConcat, viaBuilder })
        {
            Assert.DoesNotContain(TheValue, s, StringComparison.Ordinal);
            Assert.Contains(SecretString.RedactedMarker, s, StringComparison.Ordinal);
        }
    }

    // ── concatenation through the Vars-substitution stringify path ───────────────

    /// <summary>
    /// (a) GENUINE-DEFENCE.  If a regressing provider ever wrote a <see cref="SecretString"/>
    /// into <c>Vars</c>, a downstream <c>{placeholder}</c> concatenation would stringify it.
    /// The substitution helper uses <c>Convert.ToString(val, InvariantCulture)</c>, which —
    /// because the type is not <see cref="IFormattable"/> — routes through
    /// <see cref="object.ToString"/> and yields the marker.  This test reproduces that exact
    /// concatenation over a Vars-shaped map and proves a marker, not a value, is concatenated.
    /// </summary>
    [Fact]
    public void Concatenation_ViaVarsSubstitutionStringify_YieldsMarker()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apiKey"] = new SecretString(TheValue),
        };

        // Reproduce Substitute_Helpers / SecretHelper.ResolveTemplate placeholder branch:
        //   Convert.ToString(val, InvariantCulture) ?? string.Empty
        Assert.True(vars.TryGetValue("apiKey", out var val));
        var stringified = Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
        var concatenated = "key=" + stringified + "&v=1";

        Assert.DoesNotContain(TheValue, concatenated, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, concatenated, StringComparison.Ordinal);
    }
}
