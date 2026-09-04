// Vouchfx.Engine.Orchestration - SecurityPathDisclosureLedger (issue #375; lifted here by #473).
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
// validator, the artefact planner, the seed applier and the security accessor's own throw sites
// all name the declared path by construction. It cannot be enforced at a site the engine does not
// write - most acutely librdkafka, which is handed `ssl.ca.location` / `ssl.certificate.location`
// / `ssl.key.location` as resolved absolute paths and, on a load failure, builds its own message
// quoting them back. That text reaches the engine as a caught exception's Message inside a Kafka
// provider's guarded region, becomes the step's Observation, and travels into the §14 event
// stream, the --events artifact and the HTML report. Nothing between the provider and the wire
// could redact it, because nothing there knew which strings were paths. This ledger is what
// knows: it is populated at each chokepoint that holds Declared and Resolved together AND HANDS
// THE RESOLVED FORM ONWARD to code the engine does not write, and it is read at the same three
// scrub chokepoints the secret ledger is read at.
//
// THE SECOND HALF OF THAT CONDITION IS NOT DECORATION. Two sites hold both halves and record
// nothing, correctly: EnvironmentSecurityValidator.ValidatePath and SeedFixtures.ComputeContentHash
// both resolve an author-declared path and both compose their own diagnostics, which name the
// declared text by construction (#357). Neither hands the resolved form to a client library, a
// daemon or the BCL in a way that can quote it back. Recording there would add entries that can
// never match anything, and - worse for the next reader - would suggest the ledger is a general
// register of resolved paths rather than a net under foreign text.
//
// WHY IT LIVES IN Vouchfx.Engine.Orchestration (#473). It was born in Vouchfx.Engine.Runtime,
// beside the single accessor chokepoint that then populated it. #473 found the SAME leak class at
// three sibling sites that resolve an author-declared path and discard the declared half - and all
// of them are in THIS assembly, which Runtime references, so a ledger in Runtime was unreachable
// from every one of them without inverting the dependency. Moving it DOWN reaches all four
// recording sites with no cycle: Runtime -> Orchestration, and the CLI references both.
//
// IT STOPS HERE, and Abstractions is the line it must not cross. Providers and the SDK reference
// Abstractions, and none of them has any business with the engine's host paths; a ledger there
// would be reachable from twenty-five provider assemblies, and the lower the type sits the more
// places can quietly start recording into it. Orchestration is the LOWEST assembly that holds a
// recording site, which is the whole of the argument for this being its home.
//
// SCOPE AND MEMORY MODEL (§5). One instance per run, constructed beside the run's
// ResolvedSecretLedger and shared by the topology build, the topology probe's accessor and every
// scenario's, so a path registered while the topology was built is scrubbable from text emitted on
// a step path. It holds plain Default-ALC System.Strings; nothing here roots the collectible
// AssemblyLoadContext.
//
// WHAT IT DELIBERATELY DOES NOT DO. It does not scrub paths it was never handed. A resolved
// path that reaches a diagnostic without passing through one of the recording sites is invisible
// to it, exactly as ResolvedSecretLedger is blind to a secret that was never resolved. The
// property test in SecurityDiagnosticPathDisclosureTests is what covers the engine-owned
// diagnostics that name a declared path by construction; this ledger covers the sites whose text
// is built somewhere the engine does not write - librdkafka, the Docker daemon, the BCL.
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;

namespace Vouchfx.Engine.Orchestration;

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
/// also why this type stops at <c>Vouchfx.Engine.Orchestration</c> and does not descend into
/// <c>Vouchfx.Engine.Abstractions</c> — providers and the SDK reference Abstractions, and none
/// of them has any business with the engine's host paths. See this file's header for why
/// Orchestration rather than <c>Vouchfx.Engine.Runtime</c>, where it was born (#473).
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
    /// The substitution table <see cref="Scrub"/> works from: every recorded path in both its raw
    /// and JSON-escaped forms, longest-first, with zero-length forms already dropped. Built lazily
    /// on the first <see cref="Scrub"/> after a <see cref="Record"/> and nulled by
    /// <see cref="Record"/> under <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Cached because <see cref="Scrub"/> runs per EVENT LINE, and the work it repeats is
    /// not trivial.</strong> Each call snapshotted the dictionary, ran
    /// <see cref="JavaScriptEncoder"/> over every entry twice, allocated a second dictionary and
    /// sorted the result — all to rebuild a table that is written only during topology build and
    /// never again for the rest of the run. It is no longer bounded at three entries per target:
    /// since #473 it also holds every <c>security.serverArtifacts[].source</c> and every
    /// <c>environment.seed[].sql</c>, so the table scales with what the suite declares. That makes
    /// the cache worth more than when it was written, not less — the repeated work it removes grows
    /// with the same count.
    /// </para>
    /// <para>
    /// <strong>The cache changes nothing observable</strong>, which is the property that makes it
    /// safe: it is a pure function of <c>_declaredByResolved</c>, and the only mutator invalidates
    /// it while holding the same lock that guards the dictionary. A reader either sees a table
    /// built from the current contents or builds one.
    /// </para>
    /// </remarks>
    private KeyValuePair<string, string>[]? _orderedForms;

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

            // Invalidate INSIDE the lock that guards the dictionary, not beside it: a reader that
            // observed the new entry while still holding the old table would substitute against a
            // stale set, and the window would be exactly the one that matters - a path recorded
            // during topology build and quoted by a client library moments later.
            _orderedForms = null;
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

        // The Count==0 fast path is kept ahead of everything: an unsecured suite records nothing,
        // and that suite must not pay a lock-plus-build for every event line it emits.
        KeyValuePair<string, string>[] ordered;
        lock (_gate)
        {
            if (_declaredByResolved.Count == 0)
            {
                return text;
            }

            ordered = _orderedForms ??= BuildOrderedForms(_declaredByResolved);
        }

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
        // it is not reachable through the three SECURITY-material fields, whose declared text a
        // containment check has already refused if it was rooted - there, every replacement is
        // relative and no replacement can contain a form. It ALSO holds for #473's
        // `security.serverArtifacts[].source`, which goes through the same containment refusal.
        // Where it stops is exactly one field: `environment.seed[].sql` passes through no
        // rooted-path refusal (no
        // schema pattern, no containment call), so an author who writes an absolute path has an
        // absolute REPLACEMENT recorded, and the never-rescan scan is what keeps that harmless
        // rather than the shape of the inputs. An earlier version of this paragraph claimed the
        // rooted-form property held for every recorded pair; it did not survive the second and
        // third recording sites. It is written this way because `Record` is an ordinary internal method
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
    /// Builds the longest-first substitution table for <paramref name="declaredByResolved"/>:
    /// each recorded path in both its raw and JSON-escaped forms, zero-length forms dropped.
    /// </summary>
    /// <remarks>
    /// Called under <see cref="_gate"/> and never otherwise, so it may read the dictionary
    /// directly instead of snapshotting it. Extracted from <see cref="Scrub"/> when the result
    /// became cacheable; the logic and its reasoning are unchanged, which is what makes the cache
    /// a pure performance change.
    /// </remarks>
    private static KeyValuePair<string, string>[] BuildOrderedForms(
        Dictionary<string, string> declaredByResolved)
    {
        // For each recorded path the raw form maps to the raw declared text, and the encoded form
        // maps to the ENCODED declared text - substituting a raw replacement into already-escaped
        // JSON would produce text the consumer cannot decode, which is a different corruption from
        // the one being fixed.
        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (resolved, declared) in declaredByResolved)
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

        return ordered;
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
