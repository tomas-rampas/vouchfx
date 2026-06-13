// Shared file-report wiring seam for the vouchfx CLI (S09-D-01, T3).
//
// Both runner paths — ScenarioRunner.RunSuiteAsync (sequential) and
// ParallelSuiteRunner.RunParallelAsync (parallel) — call WriteFileReports IMMEDIATELY
// after their single TerminalRenderer.Render call, passing the SAME buffered JSON Lines
// event stream and the SAME diffLookup the terminal renderer already consumed.  That is
// the parity guarantee (S09-D-01): the terminal, HTML, and JUnit renderers all see byte
// -identical input, so the three reports can never disagree about what happened.
//
// Design:
//   • One static internal helper (the unit-test seam) so the wiring is exercised
//     Docker-free, directly, in Platform.Engine.Reporting.Tests.
//   • When a report path is null, nothing is written — absent --html / --junit ⇒ no
//     artifact, today's behaviour unchanged.  No directory is created for a null path.
//   • When a path is non-null, the parent directory is created if missing, the file is
//     created/overwritten, and the matching renderer writes to it.  HTML receives the
//     diffLookup (so a failed step can show an expected-vs-observed diff); JUnit does not
//     (its shape carries verdicts + messages, not per-step diffs — JunitXmlRenderer.Render
//     has no diffLookup parameter).
//   • RESILIENCE: each report is written independently and the file-system / path exception
//     family is caught PER FILE.  A caller-supplied bad --html / --junit path therefore can
//     NEVER abort the other report and can NEVER crash the run — report writing must not
//     change the already-computed verdict / exit code (a typo'd path must not manufacture a
//     CI failure from a passing suite, nor mask a real Fail).  The optional `diagnostics`
//     sink receives a single type-name-only notice per failed report (§17 redaction — never
//     ex.Message, never the resolved path).  A genuine renderer bug is NOT swallowed.
//   • UTF-8 WITHOUT a BOM: a leading BOM trips some XML/HTML consumers and CI ingesters,
//     so the encoding is `new UTF8Encoding(false)` (emit-BOM = false) — never the
//     BOM-emitting Encoding.UTF8.

using System.Text;
using System.Text.Json;

namespace Platform.Engine.Reporting;

/// <summary>
/// Writes the optional HTML and/or JUnit report files from the SAME buffered event stream
/// and the SAME render-time diff lookup the terminal renderer consumes (S09-D-01, T3).
/// </summary>
/// <remarks>
/// This is the single seam both the sequential (<c>ScenarioRunner.RunSuiteAsync</c>) and
/// parallel (<c>ParallelSuiteRunner.RunParallelAsync</c>) runner paths call after their
/// one terminal render, so the file reports are produced from the identical buffer in both
/// modes — the parity guarantee that the terminal, HTML, and JUnit views never diverge.
/// </remarks>
internal static class FileReportWriter
{
    // UTF-8 without a byte-order mark.  A BOM at the start of a JUnit XML / HTML file can
    // trip strict XML parsers and CI ingesters, so it is deliberately suppressed.
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Writes the HTML and/or JUnit report files for the supplied event stream.
    /// </summary>
    /// <param name="eventLines">
    /// The buffered JSON Lines event stream — the SAME buffer the terminal renderer was just
    /// handed, so all three renderers see identical input (parity, S09-D-01).
    /// </param>
    /// <param name="diffLookup">
    /// The render-time provider-diff lookup the terminal render used; threaded into the HTML
    /// render so a failed step can show an expected-vs-observed diff.  May be
    /// <see langword="null"/> (no diffs).  Not used by the JUnit renderer (its shape carries
    /// no per-step diff).
    /// </param>
    /// <param name="htmlPath">
    /// The destination for the self-contained HTML report, or <see langword="null"/> to write
    /// no HTML report (absent <c>--html</c> ⇒ no artifact).
    /// </param>
    /// <param name="junitPath">
    /// The destination for the JUnit XML results file, or <see langword="null"/> to write no
    /// JUnit report (absent <c>--junit</c> ⇒ no artifact).
    /// </param>
    /// <param name="diagnostics">
    /// An optional sink for one-line failure notices.  When writing a report fails for a
    /// file-system / path reason (a caller-supplied bad <c>--html</c> / <c>--junit</c> path,
    /// a permission or directory error), a single diagnostic line is written here and the
    /// failure is swallowed — <b>report writing must never change the run's verdict or exit
    /// code</b>.  Each report is attempted independently, so a bad path for one cannot abort
    /// the other.  The notice carries the exception <i>type name only</i> — never
    /// <c>ex.Message</c> nor the resolved path content — to honour the §17 redaction
    /// discipline the parallel runner already follows.  <see langword="null"/> discards the
    /// notice.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="eventLines"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// A <see langword="null"/> <b>or empty</b> path is treated identically as "no report":
    /// the matching artifact is not written and no directory is created (the
    /// <see cref="string.IsNullOrEmpty(string)"/> guard — there is no hidden third state for an
    /// empty string).  When a path is non-null and non-empty, its parent directory is created
    /// if missing and the file is created/overwritten as UTF-8 without a BOM.
    /// <para>
    /// Only the file-system / path exception family is caught per report
    /// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>,
    /// <see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
    /// <see cref="System.Security.SecurityException"/>).  A renderer / programming fault is
    /// deliberately <i>not</i> swallowed and still surfaces — the buffered event stream handed
    /// here is always valid, so a render-time throw signals a genuine bug, not a bad path.
    /// </para>
    /// </remarks>
    public static void WriteFileReports(
        IReadOnlyList<string> eventLines,
        Func<string, JsonElement, string?>? diffLookup,
        string? htmlPath,
        string? junitPath,
        TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(eventLines);

        // Each report is written independently and the file-system / path exception family is
        // caught PER FILE: a caller-supplied bad path can never abort the other report and can
        // NEVER crash the run — report writing must not change the already-computed verdict /
        // exit code.  A genuine renderer bug is NOT swallowed (the buffer is always valid).
        if (!string.IsNullOrEmpty(htmlPath))
        {
            try
            {
                using var writer = CreateWriter(htmlPath);
                HtmlRenderer.Render(eventLines, writer, diffLookup);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                diagnostics?.WriteLine(
                    $"Report write failed for 'html' (verdict unaffected): {ex.GetType().Name}");
            }
        }

        if (!string.IsNullOrEmpty(junitPath))
        {
            try
            {
                using var writer = CreateWriter(junitPath);
                JunitXmlRenderer.Render(eventLines, writer);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                diagnostics?.WriteLine(
                    $"Report write failed for 'junit' (verdict unaffected): {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Creates (or overwrites) the file at <paramref name="path"/> as a UTF-8 (no BOM)
    /// <see cref="StreamWriter"/>, creating any missing parent directories first.
    /// </summary>
    private static StreamWriter CreateWriter(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // FileMode.Create truncates an existing file, so a stale report cannot bleed through.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        return new StreamWriter(stream, Utf8NoBom);
    }
}
