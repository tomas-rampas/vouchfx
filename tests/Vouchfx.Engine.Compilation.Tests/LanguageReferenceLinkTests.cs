// #556 — StepCatalogueEntry.DocsUrl against the page it links to. Docker-free.
//
// A docsUrl anchor is only right if it equals the id the PUBLISHED site's renderer gives the
// type's heading, so the proof is three pieces of evidence, none of them the engine's slugify
// port marking its own homework:
//   1. Slugify_MatchesPythonMarkdownTocSlugify: expected values PRODUCED by the real renderer
//      (markdown.extensions.toc.slugify from Python-Markdown 3.10.3, run 2026-09-22), covering
//      the edges today's type keys never reach — Unicode folding, whitespace and hyphen runs,
//      underscores, the U+001C..U+001F separators.
//   2. DocsUrl_EveryCoreEntry_...: every Core anchor is, in the COMMITTED
//      docs/language-reference.md, both the target of the "Registered step types" link that
//      LanguageReferenceGenerator.Anchor (a separate, simpler implementation) wrote, and the id
//      toc assigns the type's heading — simulated over the WHOLE page, uniqueness suffixes
//      included, so an earlier heading that happened to slug the same would be caught.
//   3. PageUrl_...: the base is mkdocs.yml's site_url plus the page's directory URL, and
//      mkdocs.yml neither configures a custom slugify (which would void the port) nor turns
//      directory URLs off.
// Measured once outside the suite, for the record: rendering the committed page through
// Python-Markdown 3.10.3 with MkDocs' builtin toc/tables/fenced_code extensions plus the
// configured attr_list gave all 25 type headings exactly these ids, and no id carried a
// uniqueness suffix.

using System.Globalization;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class LanguageReferenceLinkTests
{
    [Theory]
    [InlineData("http.rest", "httprest")]
    [InlineData("db-assert.postgres", "db-assertpostgres")]
    [InlineData("storage-assert.s3", "storage-asserts3")]
    [InlineData("mq-publish.azureservicebus", "mq-publishazureservicebus")]
    [InlineData("vouchfx Language Reference", "vouchfx-language-reference")]
    [InlineData("Common step fields", "common-step-fields")]
    [InlineData("Registered step types (25):", "registered-step-types-25")]
    [InlineData("snake_case.step", "snake_casestep")]
    [InlineData("Mixed_Under-Score  Case", "mixed_under-score-case")]
    [InlineData("  Padded  Heading  ", "padded-heading")]
    [InlineData("a -- b", "a-b")]
    [InlineData("a---b", "a-b")]
    [InlineData("-edge-", "-edge-")]
    [InlineData(" - x - ", "-x-")]
    [InlineData("tab\tseparated", "tab-separated")]
    [InlineData("UPPER.Case", "uppercase")]
    [InlineData("C# & .NET 8", "c-net-8")]
    [InlineData("žlutý kůň", "zluty-kun")]
    [InlineData("café crème", "cafe-creme")]
    [InlineData("ﬁligature", "filigature")]
    [InlineData("① circled", "1-circled")]
    [InlineData(" nbsp ", "nbsp")]
    [InlineData("x\u001Cy", "x-y")]
    [InlineData("\u001Cedge\u001F", "edge")]
    [InlineData("\u000Bvt\u000C", "vt")]
    [InlineData("emoji \U0001F600 end", "emoji-end")]
    [InlineData("...", "")]
    [InlineData("", "")]
    public void Slugify_MatchesPythonMarkdownTocSlugify(string headingText, string expected)
    {
        Assert.Equal(expected, LanguageReferenceLink.Slugify(headingText));
    }

    [Fact]
    public void DocsUrl_EveryCoreEntry_IsAHeadingIdAndARegisteredTypesLinkOfTheCommittedReference()
    {
        var catalogue = EngineExport.BuildCatalogue(
            SuiteScaffolderTests.FullCoreRegistry(), "v", SuiteScaffolderTests.CoreProviderAssemblies());
        var reference = ReadRepositoryFile("docs", "language-reference.md");

        var typeHeadings = AssignTocHeadingIds(reference).Where(h => h.Level == 3).ToList();

        // The reference documents exactly the Core registry: the premise on which every
        // non-Core entry's docsUrl is null.
        Assert.Equal(catalogue.StepTypes.Count, typeHeadings.Count);

        var prefix = LanguageReferenceLink.PageUrl + "#";
        foreach (var entry in catalogue.StepTypes)
        {
            Assert.NotNull(entry.DocsUrl);
            Assert.StartsWith(prefix, entry.DocsUrl, StringComparison.Ordinal);
            var anchor = entry.DocsUrl![prefix.Length..];

            var heading = Assert.Single(typeHeadings, h => h.Text == entry.Type);
            Assert.Equal(anchor, heading.Id);
            Assert.Contains($"- [`{entry.Type}`](#{anchor})\n", reference, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PageUrl_IsMkdocsSiteUrlPlusTheReferencePagesDirectoryUrl()
    {
        var mkdocs = ReadRepositoryFile("mkdocs.yml");
        var lines = mkdocs.Split('\n');

        var siteUrl = Assert.Single(lines, l => l.StartsWith("site_url:", StringComparison.Ordinal))
            ["site_url:".Length..].Trim();
        Assert.Equal(LanguageReferenceLink.PageUrl, siteUrl + "language-reference/");

        // The page is docs/language-reference.md, reached through the nav.
        Assert.Contains(lines, l => l.Trim() == "docs_dir: docs");
        Assert.Contains(lines, l => l.TrimEnd().EndsWith(": language-reference.md", StringComparison.Ordinal));
        _ = ReadRepositoryFile("docs", "language-reference.md");

        // Directory URLs are MkDocs' default: turning them off would make the page
        // language-reference.html. A custom toc slugify would make the port the wrong function.
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("use_directory_urls:", StringComparison.Ordinal));
        Assert.DoesNotContain("slugify", mkdocs, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns every ATX heading outside a fenced code block the id Python-Markdown's toc
    /// extension would, in document order: <see cref="LanguageReferenceLink.Slugify"/> of the
    /// heading's text content (a code span's text is its content, so backticks are dropped),
    /// then toc's <c>unique()</c>.
    /// </summary>
    private static List<TocHeading> AssignTocHeadingIds(string markdown)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var headings = new List<TocHeading>();
        var inFence = false;

        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            var level = 0;
            while (level < line.Length && line[level] == '#')
            {
                level++;
            }

            if (inFence || level is < 1 or > 6 || level >= line.Length || line[level] != ' ')
            {
                continue;
            }

            var text = line[(level + 1)..].Trim().Replace("`", string.Empty, StringComparison.Ordinal);
            headings.Add(new TocHeading(level, text, Unique(LanguageReferenceLink.Slugify(text), used)));
        }

        return headings;
    }

    /// <summary>
    /// markdown.extensions.toc.unique: while the id is taken or empty, bump a trailing
    /// <c>_&lt;n&gt;</c> (the regex <c>^(.*)_([0-9]+)$</c>, greedy, so the LAST underscore) or
    /// append <c>_1</c>.
    /// </summary>
    private static string Unique(string id, HashSet<string> used)
    {
        while (id.Length == 0 || used.Contains(id))
        {
            var underscore = id.LastIndexOf('_');
            var suffix = underscore >= 0 ? id[(underscore + 1)..] : string.Empty;
            id = suffix.Length > 0 && suffix.All(char.IsAsciiDigit)
                ? id[..underscore] + "_"
                    + (int.Parse(suffix, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)
                : id + "_1";
        }

        used.Add(id);
        return id;
    }

    /// <summary>
    /// Reads a file from the SOURCE tree (the published page is built from the committed file,
    /// not a build copy), with LF line endings. A failure names the repository-relative path
    /// only, never a host path.
    /// </summary>
    private static string ReadRepositoryFile(params string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "No ancestor of the test output directory contains vouchfx.sln.");

        var path = Path.Combine(dir!.FullName, Path.Combine(relativePath));
        Assert.True(File.Exists(path), $"{string.Join('/', relativePath)} was not found under the repository root.");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private sealed record TocHeading(int Level, string Text, string Id);
}
