// Vouchfx.Engine.Abstractions — ResolvedSecretLedger (S11-B-01, §17).
//
// A Default-ALC record of the secret VALUES the run actually revealed, used as a
// DEFENCE-IN-DEPTH net at the reporting boundary.  Type-based redaction
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
// so nothing roots the collectible AssemblyLoadContext.
//
// LIFETIME IS THE CALLER'S CHOICE, not this type's (client-key-password REQ-010).  Built by
// SecretAccessor's one-argument constructor it is private to that accessor; passed to the
// two-argument constructor it is shared, and the engine shares ONE ledger across a whole run
// so a value resolved by the topology probe is scrubbable from text emitted on the step path
// and vice versa.  Either way it is Default-ALC and is collected with its last holder.
//
// RETAINED-PLAINTEXT FOOTPRINT: the ledger retains a plaintext `string` copy of each revealed
// value for its own lifetime — a deliberate, necessary cost (you cannot scrub a value you do
// not hold); this copy is Default-ALC, never serialised, and collected with the ledger.  A
// run-scoped ledger therefore holds those copies for the run, not for one scenario.

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;

namespace Vouchfx.Engine.Abstractions.Secrets;

/// <summary>
/// A Default-ALC record of the secret values a run has revealed, used as a defence-in-depth
/// scrub net for free-form provider observation text at the reporting boundary (§17).
/// Type-based <see cref="SecretString"/> redaction is the primary mechanism; this ledger is a
/// backstop for the one surface that is not type-checkable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope.</strong>  How far a ledger reaches is decided by whoever constructs it, not
/// here: <see cref="SecretAccessor(ISecretSourceCatalog)"/> gives an accessor a private one,
/// while <see cref="SecretAccessor(ISecretSourceCatalog, ResolvedSecretLedger)"/> lets several
/// accessors share one.  The engine shares a single instance across a run (the topology probe's
/// accessor and each scenario's), which is what makes a value resolved on one of those paths
/// scrubbable from text emitted on another.
/// </para>
/// <para>
/// The ledger records a value the moment it is resolved (see
/// <see cref="SecretAccessor"/>), and <see cref="Scrub(string)"/> replaces every occurrence
/// of a recorded value — in BOTH its raw form AND the JSON-escaped form the event stream
/// serialiser would emit it as — with <see cref="SecretString.RedactedMarker"/>.  It
/// deliberately does <strong>not</strong> attempt to recognise OTHER transforms of a revealed
/// value (base64, signatures, substrings) — those arise only from a deliberate
/// <see cref="SecretString.Reveal"/> followed by author code reshaping the bytes, which is
/// the documented escape hatch and the author's responsibility (§17).  The JSON-escaped form
/// is the one exception, and not a transform the author chose: it is the engine's OWN
/// serialiser re-encoding the value, so the engine must scrub it.
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
    /// Returns <paramref name="text"/> with every occurrence of a recorded secret value
    /// replaced by <see cref="SecretString.RedactedMarker"/> — scrubbing BOTH the raw value
    /// AND its JSON-escaped form (the form the event-stream serialiser would write it as).
    /// When nothing has been recorded, or no recorded form occurs in the text, the input is
    /// returned unchanged (the scrub is a targeted net, never a blanket rewrite).
    /// </summary>
    /// <param name="text">The free-form text to scrub (e.g. a provider observation).</param>
    /// <returns>
    /// The scrubbed text, or the original reference unchanged when no replacement applied.
    /// <see langword="null"/> in, <see langword="null"/> out.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Why two forms.</strong>  This scrub runs on a provider's raw observation text
    /// BEFORE that text is parsed and re-emitted into the schema-versioned JSON Lines event
    /// stream.  The event-stream serialiser
    /// (<c>Vouchfx.Engine.Abstractions.Events.EventStreamJson</c>) uses
    /// <c>System.Text.Json</c>'s default <see cref="JavaScriptEncoder"/>, which escapes
    /// <c>&quot;</c>, <c>\</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>+</c> and
    /// every non-ASCII character to <c>\uXXXX</c>.  When a provider's observation already
    /// embeds a revealed value in that JSON-escaped form (e.g. a base64 token containing
    /// <c>+</c> appears as <c>+</c>, or a non-ASCII secret appears as <c>\uXXXX</c>),
    /// matching only the RAW value would miss it, and the secret would survive into the
    /// on-disk <c>--events</c> artifact in recoverable escaped form (a consumer JSON-decodes
    /// it back to cleartext).  Each recorded value is therefore scrubbed in both its raw form
    /// and its <see cref="JavaScriptEncoder.Default"/>-encoded form (the SAME encoder the
    /// stream uses).  The encoded form is added only when it differs from the raw form —
    /// an ASCII value with no special characters encodes to itself.
    /// </para>
    /// <para>
    /// All forms (raw + encoded, de-duplicated) are scrubbed longest-first so that a shorter
    /// form which is a substring of a longer one — now guaranteed whenever one value yields a
    /// short raw form and a longer escaped form — cannot pre-empt the longer replacement and
    /// strand a fragment of it.  Replacement is ordinal and case-sensitive; the encoder emits
    /// upper-case hex (<c>+</c>), matching what the serialiser writes.
    /// </para>
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

        // Outside the lock: build the full set of forms to scrub.  For each recorded value
        // scrub the raw form AND the JSON-escaped form the event-stream serialiser would emit
        // (the default JavaScriptEncoder escapes '+'/non-ASCII), so a value already present in
        // escaped form in the observation text cannot survive into the --events artifact.  A
        // HashSet de-duplicates (the same encoded form can arise from distinct values, or
        // equal another value's raw form).
        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in snapshot)
        {
            forms.Add(value);

            // Same encoder the event stream serialises with — add only when it differs (an
            // ASCII value with no special characters encodes to itself).
            var encoded = JavaScriptEncoder.Default.Encode(value);
            if (!string.Equals(encoded, value, StringComparison.Ordinal))
            {
                forms.Add(encoded);
            }
        }

        var ordered = new string[forms.Count];
        forms.CopyTo(ordered);

        // Replace longest-first: a shorter form that is a substring of a longer one must not
        // pre-empt the longer replacement and strand a fragment of it.
        Array.Sort(ordered, static (a, b) => b.Length.CompareTo(a.Length));

        var result = text;
        foreach (var form in ordered)
        {
            // Ordinal, case-sensitive, all occurrences — string.Replace replaces every
            // occurrence in one pass over the current result.
            result = result.Replace(form, SecretString.RedactedMarker, StringComparison.Ordinal);
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
