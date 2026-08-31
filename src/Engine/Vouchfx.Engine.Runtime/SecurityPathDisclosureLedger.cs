// Vouchfx.Engine.Runtime - SecurityPathDisclosureLedger (issue #375).
//
// A run-scoped record of the RESOLVED absolute host paths this run handed to a third-party
// client library, together with the DECLARED text the author wrote for each one, used as a
// defence-in-depth net at the same reporting boundary ResolvedSecretLedger guards.
//
// WHY THIS IS A SEPARATE CLASS AND NOT A SECOND USE OF ResolvedSecretLedger.
// That ledger is a targeted net over secret VALUES a run's SecretAccessor actually revealed,
// and its substitution is the single generic SecretString.RedactedMarker. A filesystem path is
// not a secret: blanking it to [REDACTED] destroys the one thing the author needs in order to
// fix the problem. The house rule that #357 established for the same three fields - name the
// DECLARED path, never the resolved one - is a SUBSTITUTION, not a redaction, and it is
// per-field, so it cannot be expressed by a value ledger whose replacement is a constant.
// Registering paths in the secret ledger would also widen that ledger's semantics for every
// later reader of it. Two nets, two meanings, one boundary.
//
// WHAT IT IS FOR, precisely. #357's rule is enforced at each ENGINE-OWNED diagnostic site: the
// validator, the artefact planner, the seed applier and this accessor's own throw sites all
// name the declared path by construction. It cannot be enforced at a site the engine does not
// write - most acutely librdkafka, which is handed `ssl.ca.location` / `ssl.certificate.location`
// / `ssl.key.location` as resolved absolute paths and, on a load failure, builds its own message
// quoting them back. That text reaches the engine as a caught exception's Message inside a Kafka
// provider's guarded region, becomes the step's Observation, and travels into the §14 event
// stream, the --events artifact and the HTML report. Nothing between the provider and the wire
// could redact it, because nothing there knew which strings were paths. This ledger is what
// knows: it is populated at the single accessor chokepoint that holds Declared and Resolved
// together, and it is read at the same three scrub chokepoints the secret ledger is read at.
//
// SCOPE AND MEMORY MODEL (§5). One instance per run, constructed beside the run's
// ResolvedSecretLedger and shared by the topology probe's accessor and every scenario's, so a
// path registered while the probe ran is scrubbable from text emitted on a step path. It holds
// plain Default-ALC System.Strings; nothing here roots the collectible AssemblyLoadContext.
//
// WHAT IT DELIBERATELY DOES NOT DO. It does not scrub paths it was never handed. A resolved
// path that reaches a diagnostic without passing through the accessor's resolved-path getters
// is invisible to it, exactly as ResolvedSecretLedger is blind to a secret that was never
// resolved. The property test in SecurityDiagnosticPathDisclosureTests is what covers the
// sibling sites; this ledger covers the one site that is structurally out of the engine's hands.
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// A run-scoped map of resolved absolute security-material paths to the declared text the
/// author wrote for them, used to substitute the declared form back into free-form diagnostic
/// text an out-of-engine client library built (issue #375).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Substitution, not redaction.</strong> Every recorded path is replaced by its OWN
/// declared text, which is what <c>EnvironmentSecurityValidator.ValidatePath</c>,
/// <c>ServerArtifactInjection.Plan</c> and <c>SecurityConfigurationAccessor</c>'s own throw
/// sites already write for the same fields (#357). The result is a diagnostic an author can
/// act on that discloses nothing about the host's directory layout - never
/// <c>[REDACTED]</c>, which would be strictly worse than the leak for everything except the
/// disclosure.
/// </para>
/// <para>
/// <strong>Both forms are scrubbed</strong> - the raw path and the
/// <see cref="JavaScriptEncoder.Default"/>-encoded form the event-stream serialiser would write
/// it as. That is not a theoretical case on Windows: a resolved path is full of
/// <c>\</c>, which encodes to <c>\\</c>, so a path already embedded in serialised JSON would
/// survive a raw-only match and be recoverable from the on-disk <c>--events</c> artifact by any
/// consumer that JSON-decodes it. The same reasoning, and the same encoder, as
/// <see cref="Vouchfx.Engine.Abstractions.Secrets.ResolvedSecretLedger"/>.
/// </para>
/// <para>
/// Thread-safe: parallel scenarios share one instance, and a lazily-loaded certificate may be
/// forced from more than one step at a time.
/// </para>
/// <para>
/// <strong>A PUBLIC type with INTERNAL members, deliberately.</strong> It has to be public
/// because it appears on <c>ScenarioRunner.RunPlannedScenarioAgainstKeptTopologyAsync</c>, which
/// is public for the CLI's <c>WatchRunner</c> — C# forbids a less-accessible parameter type
/// there, exactly as <see cref="Vouchfx.Engine.Abstractions.Secrets.ResolvedSecretLedger"/> is
/// public for the same reason. Keeping every member internal makes it an OPAQUE token to anyone
/// outside this assembly's <c>InternalsVisibleTo</c> friends: an embedder can hold one and pass
/// it along, and can only ever pass <see langword="null"/>, which is today's behaviour. It is
/// also why this type lives in <c>Vouchfx.Engine.Runtime</c> and not in
/// <c>Vouchfx.Engine.Abstractions</c> — providers and the SDK reference Abstractions, and none
/// of them has any business with the engine's host paths.
/// </para>
/// </remarks>
public sealed class SecurityPathDisclosureLedger
{
    /// <summary>
    /// Resolved absolute path to the declared text it substitutes back to. Ordinal, because a
    /// path comparison that is not ordinal is a different path comparison on every platform.
    /// </summary>
    /// <remarks>
    /// A dictionary rather than a set: the replacement is per-entry, which is the whole reason
    /// this type is not <c>ResolvedSecretLedger</c>. Last write wins for a repeated resolved
    /// path, which can only happen when two fields resolve to the same file - in which case
    /// either declared text is a correct answer for it.
    /// </remarks>
    private readonly Dictionary<string, string> _declaredByResolved = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    /// <summary>
    /// Records that <paramref name="resolved"/> was handed out for the field the author declared
    /// as <paramref name="declared"/>, so a later diagnostic quoting the resolved form can have
    /// the declared form substituted back in.
    /// </summary>
    /// <param name="resolved">The absolute host path handed to a client library.</param>
    /// <param name="declared">The author's own text for the same field.</param>
    /// <remarks>
    /// <para>
    /// A null, empty or whitespace-only value on EITHER side is ignored. An empty resolved path
    /// would match everywhere and rewrite unrelated text wholesale; an empty declared form would
    /// delete the path from the diagnostic rather than replace it, which reads as corruption.
    /// </para>
    /// <para>
    /// A resolved path EQUAL to its declared text is also ignored: substituting a string for
    /// itself is a no-op that only costs a pass over the text, and the equal case means the
    /// author already wrote the absolute path (which the validator refuses on every production
    /// path anyway).
    /// </para>
    /// </remarks>
    internal void Record(string? resolved, string? declared)
    {
        if (string.IsNullOrWhiteSpace(resolved)
            || string.IsNullOrWhiteSpace(declared)
            || string.Equals(resolved, declared, StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            _declaredByResolved[resolved] = declared;
        }
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every occurrence of a recorded resolved path -
    /// raw or JSON-escaped - replaced by that path's declared text. When nothing has been
    /// recorded, or no recorded form occurs, the input is returned unchanged: the scrub is a
    /// targeted substitution, never a blanket rewrite.
    /// </summary>
    /// <param name="text">The free-form diagnostic or observation text.</param>
    /// <returns>
    /// The substituted text, or the original reference when nothing applied.
    /// <see langword="null"/> in, <see langword="null"/> out.
    /// </returns>
    [return: NotNullIfNotNull(nameof(text))]
    internal string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        KeyValuePair<string, string>[] snapshot;
        lock (_gate)
        {
            if (_declaredByResolved.Count == 0)
            {
                return text;
            }

            snapshot = new KeyValuePair<string, string>[_declaredByResolved.Count];
            ((ICollection<KeyValuePair<string, string>>)_declaredByResolved).CopyTo(snapshot, 0);
        }

        // Outside the lock: build (form -> replacement) pairs. For each recorded path the raw
        // form maps to the raw declared text, and the encoded form maps to the ENCODED declared
        // text - substituting a raw replacement into already-escaped JSON would produce text the
        // consumer cannot decode, which is a different corruption from the one being fixed.
        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (resolved, declared) in snapshot)
        {
            forms[resolved] = declared;

            var encodedResolved = JavaScriptEncoder.Default.Encode(resolved);
            if (!string.Equals(encodedResolved, resolved, StringComparison.Ordinal)
                && !forms.ContainsKey(encodedResolved))
            {
                forms[encodedResolved] = JavaScriptEncoder.Default.Encode(declared);
            }
        }

        // ZERO-LENGTH FORMS ARE DROPPED HERE, AND THIS GUARD IS LOAD-BEARING RATHER THAN
        // DEFENSIVE. `string.CompareOrdinal(text, index, form, 0, 0)` returns 0 - an empty form
        // matches vacuously at EVERY position - so the scan below would take the match, append the
        // replacement, advance `index` by the form's length of zero, and do it again forever: an
        // infinite loop growing a StringBuilder until the process dies.
        //
        // IT IS ALSO A FAILURE-MODE REGRESSION THIS GUARD REPAYS. The `string.Replace` shape the
        // single pass replaced THREW `ArgumentException` on an empty `oldValue`, loudly and
        // immediately. Without this filter the rewrite would have converted that throw into a
        // silent hang, which is strictly worse: a crash names itself and a wedged suite does not.
        //
        // Unreachable from today's callers - `Record` rejects null, empty and whitespace on both
        // sides, and `JavaScriptEncoder.Encode` of a non-empty string is non-empty - and written
        // anyway, for the reason this file's own test remarks give: "the callers happen not to do
        // that" is not evidence, it is a fact about today's callers.
        var ordered = forms.Where(static f => f.Key.Length > 0).ToArray();

        // Longest form first: a recorded directory that is a prefix of a recorded file - the
        // ordinary shape when caCert and clientCert sit in one folder - must not pre-empt the
        // longer replacement and strand the tail of the path it was part of.
        Array.Sort(ordered, static (a, b) => b.Key.Length.CompareTo(a.Key.Length));

        // ONE PASS, AND NEVER OVER TEXT ALREADY SUBSTITUTED. The obvious implementation is a
        // sequence of string.Replace calls, one per form, and it has a defect this one does not:
        // each Replace runs over the WHOLE accumulated result, including the declared text an
        // earlier form just spliced in. A declared text that happens to contain a later form's
        // recorded string is then rewritten inside - `sub/suite/ca.pem` becoming `subca.pem`,
        // a file named after nothing.
        //
        // BE PRECISE ABOUT SEVERITY, because overstating it is how a fix gets reverted later: the
        // consequence is a MISNAMED path in a diagnostic, never a disclosure - the substitution
        // only ever makes text shorter and more declared, so no host layout escapes this way. And
        // with today's callers it is not reachable at all: every recorded form is a rooted
        // absolute path and every replacement is the author's relative text, so no replacement can
        // contain a form. It is written this way because `Record` is an ordinary internal method
        // with no such constraint on it, this class is NEW so there is no house-style debt to
        // weigh against, and a scan that cannot revisit its own output is not more code than one
        // that can.
        //
        // The scan walks the input once. At each position it tries the recorded forms
        // longest-first, so a directory that prefixes a file still loses to the file; on a match
        // it appends the REPLACEMENT and jumps past the matched form, which is what makes the
        // output unreachable to the remaining forms.
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var matched = false;
            foreach (var (form, replacement) in ordered)
            {
                if (form.Length <= text.Length - index
                    && string.CompareOrdinal(text, index, form, 0, form.Length) == 0)
                {
                    builder.Append(replacement);
                    index += form.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                builder.Append(text[index]);
                index++;
            }
        }

        // The ORIGINAL reference when nothing matched, not an equal copy: the contract above says
        // the scrub is a targeted substitution and callers compare results ordinally.
        var result = builder.ToString();
        return string.Equals(result, text, StringComparison.Ordinal) ? text : result;
    }

    /// <summary>
    /// The number of distinct resolved paths currently recorded. For tests and diagnostics.
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _declaredByResolved.Count;
            }
        }
    }
}
