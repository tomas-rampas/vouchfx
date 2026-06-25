// Platform.Engine.Abstractions — ResolvedSecretLedger (S11-B-01, §17).
//
// A per-scenario, Default-ALC record of the secret VALUES the run actually revealed,
// used as a DEFENCE-IN-DEPTH net at the reporting boundary.  Type-based redaction
// (the SecretString carrier — ToString/converter/no-IFormattable) remains the PRIMARY
// mechanism: every structured surface (event fields, captured-var thread, every
// renderer) is redacted by construction because it only ever handles the carrier or
// metadata, never a free-form value.  This ledger exists ONLY to scrub the one
// free-form, author-built surface the engine cannot type-check — a provider's
// OBSERVATION text (most acutely the script.csharp `__obs = __ex.Message;` path, where
// a thrown exception's message is spliced verbatim) — before that text enters the
// schema-versioned JSON Lines event stream and is re-emitted byte-for-byte by the raw
// --events artifact.
//
// WHY THIS IS NOT THE PRIMARY MECHANISM (and must never become it):
//   • String-matching a value against arbitrary text is inherently incomplete — it
//     cannot catch a value the author TRANSFORMED after Reveal() (base64, an HMAC
//     signature, a substring).  Those transforms are the documented, auditable
//     Reveal() escape hatch (§17): once an author reveals a value and reshapes it, the
//     engine has no value to recognise.  The ledger therefore redacts only the value
//     it actually revealed, appearing verbatim — the realistic accident (an exception
//     message that interpolated a revealed value), not every theoretical encoding.
//   • It runs at the reporting boundary, not at the carrier, so it changes no
//     injection-sink behaviour and bakes no value into IL (compile-once preserved).
//
// MEMORY MODEL (§5): the ledger holds plain System.String values, owned by the
// Default-ALC SecretAccessor that the runner already holds.  Recording from inside the
// collectible script's `accessor.Resolve(...)` call executes the Default-ALC method
// body, which adds to a Default-ALC set — no static, no handle bridging the boundary,
// so nothing roots the collectible AssemblyLoadContext.  The ledger is scoped to the
// per-scenario accessor and is collected with it.
//
// RETAINED-PLAINTEXT FOOTPRINT: the ledger now retains a plaintext `string` copy of each
// revealed value for the scenario's lifetime — a deliberate, necessary cost (you cannot
// scrub a value you do not hold); this copy is Default-ALC, per-scenario, never serialised,
// and collected with the accessor.

using System;
using System.Collections.Generic;

namespace Platform.Engine.Abstractions.Secrets;

/// <summary>
/// A per-scenario, Default-ALC record of the secret values a run has revealed, used as a
/// defence-in-depth scrub net for free-form provider observation text at the reporting
/// boundary (§17).  Type-based <see cref="SecretString"/> redaction is the primary
/// mechanism; this ledger is a backstop for the one surface that is not type-checkable.
/// </summary>
/// <remarks>
/// <para>
/// The ledger records a value the moment it is resolved (see
/// <see cref="SecretAccessor"/>), and <see cref="Scrub(string)"/> replaces every verbatim
/// occurrence of a recorded value with <see cref="SecretString.RedactedMarker"/>.  It
/// deliberately does <strong>not</strong> attempt to recognise transforms of a revealed
/// value (encodings, signatures, substrings) — those arise only from a deliberate
/// <see cref="SecretString.Reveal"/> followed by author code reshaping the bytes, which is
/// the documented escape hatch and the author's responsibility (§17).
/// </para>
/// <para>
/// Empty and whitespace-only values are never recorded: scrubbing them would rewrite
/// unrelated text wholesale, and an empty secret is not a meaningful leak.
/// </para>
/// </remarks>
public sealed class ResolvedSecretLedger
{
    // Ordinal set of revealed values seen this scenario.  A HashSet gives O(1) record and
    // de-duplicates when the same secret is resolved at many sinks.  Plain strings only —
    // never a SecretString — so the ledger cannot itself become a value-comparison oracle
    // across the boundary (it lives wholly in the Default ALC).
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    /// <summary>
    /// Records <paramref name="value"/> as a revealed secret to be scrubbed from later
    /// observation text.  A <see langword="null"/>, empty, or whitespace-only value is
    /// ignored (scrubbing such a value would corrupt unrelated text and it is not a
    /// meaningful leak).
    /// </summary>
    /// <param name="value">The revealed secret value to remember.</param>
    /// <remarks>
    /// Thread-safe: a RETRY step or a parallel scenario sharing one accessor instance may
    /// resolve concurrently.  Recording is idempotent.
    /// </remarks>
    public void Record(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lock (_gate)
        {
            _values.Add(value);
        }
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every verbatim occurrence of a recorded
    /// secret value replaced by <see cref="SecretString.RedactedMarker"/>.  When nothing
    /// has been recorded, or no recorded value occurs in the text, the input is returned
    /// unchanged (the scrub is a targeted net, never a blanket rewrite).
    /// </summary>
    /// <param name="text">The free-form text to scrub (e.g. a provider observation).</param>
    /// <returns>
    /// The scrubbed text, or the original reference unchanged when no replacement applied.
    /// <see langword="null"/> in, <see langword="null"/> out.
    /// </returns>
    /// <remarks>
    /// Longer recorded values are scrubbed first so that a value which is a substring of
    /// another (rare, but possible across multiple secrets) cannot leave a residual
    /// fragment of the longer value behind.  Replacement is ordinal and case-sensitive —
    /// a secret value is matched exactly as it was revealed.
    /// </remarks>
    public string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string[] snapshot;
        lock (_gate)
        {
            if (_values.Count == 0)
            {
                return text;
            }

            snapshot = new string[_values.Count];
            _values.CopyTo(snapshot);
        }

        // Replace longest-first: a shorter recorded value that is a substring of a longer
        // one must not pre-empt the longer replacement and strand a fragment of it.
        Array.Sort(snapshot, static (a, b) => b.Length.CompareTo(a.Length));

        var result = text;
        foreach (var value in snapshot)
        {
            // Ordinal, case-sensitive, all occurrences — string.Replace replaces every
            // occurrence in one pass over the current result.
            result = result.Replace(value, SecretString.RedactedMarker, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// The number of distinct values currently recorded.  Exposed for diagnostics and
    /// tests only; it reveals a count, never a value.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }
}
