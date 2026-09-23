// Engine schema and catalogue export — the language-reference deep link (#556).
//
// StepCatalogueEntry.DocsUrl points at a step type's section of the PUBLISHED language
// reference. That page is docs/language-reference.md (generated from the composed schema by
// LanguageReferenceGenerator and golden-gated), rendered by `mkdocs build` with
// mkdocs-material, so the anchor a link must name is the id the site's Markdown renderer
// assigns to the `### `<type>`` heading — not an id this engine chooses. mkdocs.yml
// configures the `toc` extension with `permalink: true` and NO custom `slugify`, so the id is
// Python-Markdown's DEFAULT toc slugify applied to the heading's text content (the type key:
// the backticks delimit a code span, whose text content is the type itself).
//
// WHY A PORT RATHER THAN A SIMPLER RULE: every type key today is lowercase [a-z0-9.-], on
// which "drop the dot" would agree with the renderer. But LanguageReferenceGenerator's own
// Anchor() is already a simpler rule than the renderer's (it drops underscores, which
// Python-Markdown keeps, and does not collapse runs of hyphens, which Python-Markdown does),
// so the two agree only on today's inputs. A published URL should follow the renderer, not
// either approximation, so this is a faithful port, step for step, of
// markdown.extensions.toc.slugify(value, '-') (Python-Markdown 3.10.3, the version measured):
//
//   value = unicodedata.normalize('NFKD', value)
//   value = value.encode('ascii', 'ignore').decode('ascii')
//   value = re.sub(r'[^\w\s-]', '', value).strip().lower()
//   return re.sub(r'[{}\s]+'.format(separator), separator, value)
//
// Two details a casual port gets wrong, both pinned by measured vectors in
// LanguageReferenceLinkTests: Python's \s treats U+001C..U+001F as whitespace, so an interior
// separator becomes a hyphen ("x\x1cy" -> "x-y") where a port with the usual six-character
// whitespace set drops it ("xy"); and NFKD folds compatibility characters (NBSP, ligatures,
// circled digits) to ASCII BEFORE the non-ASCII characters are dropped. The parity test against
// the committed reference, not this comment, is the proof that every Core anchor resolves.
//
// Toc's unique() suffixing (`_1`, `_2`) is deliberately NOT applied here: it depends on every
// other heading on the page, which this engine does not render. The parity test simulates it
// over the whole committed page and asserts no type heading is suffixed.

using System.Text;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Builds the published language-reference URL for a step type's section.
/// </summary>
internal static class LanguageReferenceLink
{
    /// <summary>
    /// The published language-reference page: <c>mkdocs.yml</c>'s <c>site_url</c> followed
    /// by the directory URL MkDocs gives <c>docs/language-reference.md</c> (directory URLs
    /// are MkDocs' default and the repository does not disable them). Pinned to the
    /// configuration by <c>LanguageReferenceLinkTests</c>.
    /// </summary>
    internal const string PageUrl = "https://vouchfx.io/language-reference/";

    /// <summary>
    /// Returns the absolute URL of <paramref name="stepType"/>'s section of the published
    /// language reference.
    /// </summary>
    /// <param name="stepType">A dotted <c>family.provider</c> key, e.g. <c>http.rest</c>.</param>
    internal static string For(string stepType) => PageUrl + "#" + Slugify(stepType);

    /// <summary>
    /// A port of Python-Markdown's default <c>toc</c> slugify with the <c>-</c> separator:
    /// NFKD-normalise, drop every non-ASCII character, drop every character that is not a
    /// word character, whitespace or a hyphen, trim whitespace, lower-case, then collapse each
    /// run of hyphens and whitespace to a single hyphen.
    /// </summary>
    /// <param name="value">A heading's text content.</param>
    /// <returns>The heading id the renderer assigns before any uniqueness suffix.</returns>
    internal static string Slugify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // unicodedata.normalize('NFKD', value).encode('ascii', 'ignore'): a surrogate pair is
        // two code units above U+007F, so an astral character is dropped whole, as in Python.
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            // re.sub(r'[^\w\s-]', '', value). On the ASCII-only string left by the step above,
            // Python's Unicode \w is exactly [A-Za-z0-9_].
            if (c <= '\u007F' && (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || IsPythonWhitespace(c)))
            {
                kept.Append(c);
            }
        }

        // .strip().lower(), then re.sub(r'[-\s]+', '-', value).
        var trimmed = TrimPythonWhitespace(kept.ToString()).ToLowerInvariant();
        var slug = new StringBuilder(trimmed.Length);
        var inSeparatorRun = false;
        foreach (var c in trimmed)
        {
            if (c == '-' || IsPythonWhitespace(c))
            {
                if (!inSeparatorRun)
                {
                    slug.Append('-');
                    inSeparatorRun = true;
                }

                continue;
            }

            slug.Append(c);
            inSeparatorRun = false;
        }

        return slug.ToString();
    }

    /// <summary>
    /// Python's <c>str.isspace()</c> — the definition both <c>\s</c> and <c>str.strip()</c>
    /// use — restricted to ASCII, which is all that survives the NFKD/ASCII step. It includes
    /// the four information separators U+001C..U+001F, which a port listing only space, tab,
    /// CR, LF, VT and FF would miss: measured, <c>"x\x1cy"</c> slugs to <c>x-y</c>, where that
    /// narrower port yields <c>xy</c> (at either edge the two agree, since a separator is
    /// stripped by one and dropped by the other).
    /// </summary>
    private static bool IsPythonWhitespace(char c) =>
        c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u001F';

    private static string TrimPythonWhitespace(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsPythonWhitespace(value[start]))
        {
            start++;
        }

        while (end > start && IsPythonWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }
}
