// Vouchfx.Cli.Tests - issue #379: the CLI's and the engine's RUNTIME OUTPUT strings are ASCII.
//
// THE DEFECT, measured differentially inside one trx during the slice-F verification. The same
// security-confirmation line, written from one source string, came out two ways:
//
//     in-process row : client identity resolved — the broker answered...
//     CLI subprocess : client identity resolved - the broker answered...
//
// The first row carries a REAL em dash and the second a real hyphen - this comment is
// trivia, so the gate below cannot see it, and writing both rows in ASCII (as the first
// draft of this file did) left two byte-identical lines labelled as differing.
//
// The child process degrades non-ASCII through Console.OutputEncoding, which on Windows defaults to
// the active codepage rather than UTF-8. An em dash with no representation there best-fit-maps to a
// hyphen - not to the usual '?', which is what makes it invisible.
//
// IMPACT TODAY: none. IMPACT LATENT: a mystifying assertion failure. Any future assertion on CLI
// output that happens to span an em dash fails on such a host with a diff between two strings that
// render identically in a terminal, and the cause is nowhere near the assertion. Separately, this is
// USER-VISIBLE output: a run piped to a CI log keeps the mangling in the artefact.
//
// THE APPROVED FIX IS OPTION 2 of the issue: keep the strings ASCII, rather than setting
// Console.OutputEncoding (option 1), which trades one mangling for another on a terminal that
// cannot render UTF-8 and has to be reasoned about separately for redirected output. The em dashes
// were stylistic; a tool whose output lands in CI logs of unknown encoding should not depend on
// them.
//
// WHY A SOURCE CENSUS. The defect is a property of every output string, present and future, and no
// behavioural test can cover strings nobody has written yet. This file is the gate that keeps the
// class closed: it parses the boundary's C# with Roslyn and asserts that no literal token carries a
// non-ASCII character.
//
// ROSLYN, NOT A REGEX. The distinction this gate has to make - literal versus comment - is exactly
// the one a regex cannot make reliably over verbatim strings, raw strings, interpolated holes and
// `//` inside a URL. Roslyn's lexer answers it exactly: comments and XML documentation are TRIVIA
// and never appear as tokens, so the boundary between "prose, which keeps its em dashes deliberately"
// and "output, which may not" is drawn by the compiler rather than by this file's cleverness.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Vouchfx.Cli;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Issues #379 and #472: fixed output formatting and public schema descriptions use ASCII.
/// Observation data remains Unicode; generated CSX comments and frozen helper bodies are excluded.
/// </summary>
public sealed class AsciiRuntimeOutputCensusTests
{
    /// <summary>
    /// CLI and engine C# literals are the original #379 output boundary. The #472 tests
    /// below additionally cover provider diff-format methods and their local call graph,
    /// SDK literals outside frozen generated CSX Source fields, and public schema descriptions.
    /// Provider Emit/helpers remain outside the literal census because their raw strings contain
    /// generated C# comments; changing them would also move frozen SDK helper-body contracts.
    /// User-authored observation values, examples, defaults, enums and patterns may contain Unicode.
    /// </summary>
    private static readonly string[] s_boundary =
    {
        Path.Combine("src", "Cli"),
        Path.Combine("src", "Engine"),
    };

    /// <summary>
    /// Every token kind that carries author-written character data. Comments and XML documentation
    /// are trivia in Roslyn's model and are therefore absent from this list by construction, not by
    /// filtering - which is the whole reason the gate is written this way.
    /// </summary>
    private static readonly SyntaxKind[] s_literalKinds =
    {
        SyntaxKind.StringLiteralToken,
        SyntaxKind.Utf8StringLiteralToken,
        SyntaxKind.SingleLineRawStringLiteralToken,
        SyntaxKind.MultiLineRawStringLiteralToken,
        SyntaxKind.Utf8SingleLineRawStringLiteralToken,
        SyntaxKind.Utf8MultiLineRawStringLiteralToken,
        SyntaxKind.InterpolatedStringTextToken,
        SyntaxKind.CharacterLiteralToken,
    };

    /// <summary>
    /// The ASCII replacements this repository uses, quoted in the failure message so the fix is
    /// stated rather than left to be guessed at differently each time.
    /// </summary>
    private const string ReplacementGuidance =
        "  em dash / en dash  ->  '-'  (the surrounding spaces already read as a dash)\n"
        + "  ellipsis           ->  '...'\n"
        + "  right arrow        ->  '->'\n"
        + "  section sign       ->  'section ' (e.g. '(section 12.1)', '(DSL section 3.2)')\n"
        + "  curly quotes       ->  the straight ASCII quote\n";

    /// <summary>
    /// No literal in the boundary carries a non-ASCII character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reports EVERY offender, not the first.</strong> A gate that fails on one site at a
    /// time turns a sweep into as many edit-build-run cycles as there are strings, which is how a
    /// gate comes to be suppressed rather than satisfied.
    /// </para>
    /// <para>
    /// <strong>The file count is asserted non-zero first</strong>, because "no offenders" is also
    /// what a census that found no FILES reports - the failure mode every source-census test has,
    /// reached here by a directory rename. A census matching nothing passes for free, and this is
    /// the check that stops it.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoRuntimeOutputLiteral_ContainsANonAsciiCharacter()
    {
        var repoRoot = RepositoryRoot();
        var files = BoundaryFiles(repoRoot).ToList();

        Assert.True(
            files.Count > 0,
            "This census found no .cs files under " + string.Join(" or ", s_boundary)
            + ". Most likely a project moved. Point the boundary at where it went rather than "
            + "leaving it matching nothing: a census that reads no files reports no offenders and "
            + "passes for free, which is the one way this file can stop guarding anything without "
            + "saying so.");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(
                SourceText.From(text),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: file);

            foreach (var token in tree.GetRoot().DescendantTokens())
            {
                if (Array.IndexOf(s_literalKinds, token.Kind()) < 0)
                {
                    continue;
                }

                // token.Text (the SOURCE SPELLING) and not token.ValueText (the decoded value),
                // and the trade-off is exact rather than incidental.
                //
                // WHAT THIS COSTS: a literal written as an ESCAPE - "\u2014" - is invisible to this
                // gate and still emits an em dash at run time. That hole is real and is accepted.
                //
                // WHAT IT BUYS: ValueText would redden SecurityConfigurationAccessor's own
                // sanitiser constants, which name control characters as '\u0080' / '\u009f'
                // precisely because that file's rule is that a control character is written as an
                // escape and never as a raw byte. Those are correct code doing the right thing, and
                // a gate that fails them teaches the next author to suppress the gate.
                //
                // The defect #379 records is a TYPED character - somebody wrote an em dash because
                // it reads nicely - and that is what a source-spelling check catches. An escape is a
                // deliberate act by an author who already knows what the character is; this gate
                // does not try to stop it, and saying so here is what stops the omission being read
                // as coverage.
                var found = token.Text.Where(c => c > 0x7F).Distinct().ToArray();
                if (found.Length == 0)
                {
                    continue;
                }

                var line = tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;
                var codes = string.Join(
                    ", ",
                    found.Select(c =>
                        "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                        + " '" + c + "'"));

                offenders.Add(
                    $"  {Path.GetRelativePath(repoRoot, file)}:{line}  {codes}\n"
                    + $"      {Excerpt(token.Text)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} literal(s) in the CLI / engine runtime-output surface contain "
            + "non-ASCII characters (issue #379).\n"
            + "\n"
            + "These strings reach a console, a CI log, the section 14 event stream or a rendered report. "
            + "On a Windows host with a legacy console codepage the child process degrades them "
            + "through Console.OutputEncoding - an em dash best-fit-maps to a hyphen, silently - so "
            + "an assertion spanning one fails with a diff between two strings that look identical. "
            + "Use the ASCII spelling:\n"
            + "\n"
            + ReplacementGuidance
            + "\n"
            + "COMMENTS AND XML DOCUMENTATION ARE NOT AFFECTED and must not be changed to satisfy "
            + "this gate: they are Roslyn trivia, never tokens, so nothing below is one. This "
            + "repository writes British English prose with typographic punctuation deliberately.\n"
            + "\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void CoreProviderDiffFormattingLiterals_AreAscii()
    {
        var repoRoot = RepositoryRoot();
        var providerRoot = Path.Combine(repoRoot, "src", "Providers", "Core");
        var classes = SourceFiles(providerRoot)
            .SelectMany(file => ParseSource(file).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(type => type.BaseList?.Types.Any(t => t.Type.ToString().EndsWith("IStepDiffRenderer", StringComparison.Ordinal)) == true)
            .ToArray();
        var registered = ProviderRegistryFactory.BuildCoreRegistry().All
            .Count(provider => provider.Instance is IStepDiffRenderer);
        Assert.True(registered >= 9, "The provider diff census must include all nine table renderers.");
        Assert.Equal(registered, classes.Length);

        foreach (var type in classes)
        {
            var methods = type.Members.OfType<MethodDeclarationSyntax>()
                .ToLookup(method => method.Identifier.ValueText, StringComparer.Ordinal);
            var pending = new Queue<string>(new[] { "CanRender", "RenderDiff" });
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var name))
            {
                if (!visited.Add(name))
                    continue;

                var declarations = methods[name].ToArray();
                Assert.NotEmpty(declarations);
                foreach (var method in declarations)
                {
                    AssertAsciiLiterals(method.DescendantTokens(), type.Identifier.ValueText + "." + name);
                    foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        // Local helper calls belong to the formatting boundary. External client,
                        // JSON and StringBuilder methods do not splice generated provider CSX.
                        var localName = call.Expression switch
                        {
                            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.ValueText,
                            _ => null,
                        };
                        if (localName is not null && methods.Contains(localName))
                            pending.Enqueue(localName);
                    }
                }
            }
        }
    }

    [Fact]
    public void SdkOutputLiterals_OutsideFrozenCsxSourceFields_AreAscii()
    {
        var files = SourceFiles(Path.Combine(RepositoryRoot(), "src", "Sdk")).ToArray();
        Assert.NotEmpty(files);
        var frozenFields = 0;
        foreach (var file in files)
        {
            var root = ParseSource(file).GetRoot();
            var sources = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.PublicKeyword)
                    && field.Modifiers.Any(SyntaxKind.ConstKeyword)
                    && field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "Source"))
                .ToArray();
            frozenFields += sources.Length;
            AssertAsciiLiterals(root.DescendantTokens()
                .Where(token => !sources.Any(field => field.Span.Contains(token.Span))), file);
        }

        Assert.True(frozenFields >= 4, "The four frozen SDK CSX Source bodies must retain their explicit exclusion.");
    }

    [Fact]
    public void PublicSchemaDescriptions_AreAscii_InProvidersResourcesAndExports()
    {
        var repoRoot = RepositoryRoot();
        var registry = ProviderRegistryFactory.BuildCoreRegistry();
        Assert.Equal(25, registry.All.Count);
        foreach (var provider in registry.All)
        {
            Assert.NotNull(provider.SchemaFragment);
            AssertAsciiDescriptions(provider.SchemaFragment!.Json, provider.Kind.ToString());
        }

        foreach (var path in new[]
        {
            "src/Engine/Vouchfx.Engine.Compilation/Schema/root-language-schema.json",
            "src/Engine/Vouchfx.Engine.Orchestration/Environments/compose-environment-schema.json",
            "tests/Vouchfx.Engine.Compilation.Tests/Golden/composed-schema.v1.json",
            "tools/vscode-vouchfx/src/schema/composed-schema.v1.json",
        })
        {
            AssertAsciiDescriptions(File.ReadAllText(Path.Combine(repoRoot, path)), path);
        }

        AssertAsciiDescriptions(EngineExport.ComposeSchemaJson(registry), "composed CLI export");
        AssertAsciiDescriptions(ComposeEnvironmentDriver.ExportConfigurationSchema(), "Compose adapter export");
    }

    private static void AssertAsciiDescriptions(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var count = 0;
        Walk(document.RootElement, "");
        Assert.True(count > 0, "No schema descriptions were inspected in " + source);

        void Walk(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "description" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        count++;
                        Assert.True(property.Value.GetString()!.All(character => character <= 0x7f),
                            source + path + "/description contains non-ASCII punctuation.");
                    }
                    else if (property.Name is not ("examples" or "default" or "const" or "enum"))
                    {
                        Walk(property.Value, path + "/" + property.Name);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var value in element.EnumerateArray())
                    Walk(value, path + "/" + index++);
            }
        }
    }

    private static void AssertAsciiLiterals(IEnumerable<SyntaxToken> tokens, string source)
    {
        var offenders = tokens.Where(token => Array.IndexOf(s_literalKinds, token.Kind()) >= 0)
            .Where(token => token.Text.Any(character => character > 0x7f))
            .Select(token => source + ":" + (token.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                + " " + Excerpt(token.Text)).ToArray();
        Assert.True(offenders.Length == 0, string.Join("\n", offenders));
    }

    private static SyntaxTree ParseSource(string path) => CSharpSyntaxTree.ParseText(
        File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path);

    private static IEnumerable<string> SourceFiles(string root)
    {
        Assert.True(Directory.Exists(root), "Missing census root: " + root);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));
    }

    /// <summary>
    /// One line of context for a failure message, with newlines made visible so a multi-line raw
    /// string does not reformat the report around itself.
    /// </summary>
    private static string Excerpt(string raw)
    {
        var flattened = raw.Replace("\r", "\\r", StringComparison.Ordinal)
                           .Replace("\n", "\\n", StringComparison.Ordinal);

        return flattened.Length <= 160 ? flattened : flattened[..160] + "...";
    }

    /// <summary>
    /// Every <c>.cs</c> file inside the boundary, excluding build output.
    /// </summary>
    /// <remarks>
    /// <c>obj</c> and <c>bin</c> are excluded because they hold GENERATED sources - the
    /// <c>AssemblyAttribute</c> file this repository's csproj files emit among them - which no
    /// author can edit and which this gate has no business failing on.
    /// </remarks>
    private static IEnumerable<string> BoundaryFiles(string repoRoot)
    {
        var obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        var bin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;

        foreach (var relative in s_boundary)
        {
            var root = Path.Combine(repoRoot, relative);

            // PER-ENTRY, not aggregate. The caller's non-empty check is over the WHOLE census, so
            // with `src/Engine` present and `src/Cli` moved it still sees hundreds of files and
            // passes - and the CLI, which is where issue #379 was actually measured, would have
            // silently left the gate. A skipped root is the one failure a census cannot report by
            // finding nothing.
            Assert.True(
                Directory.Exists(root),
                $"Boundary root '{relative}' does not exist under '{repoRoot}'. This census reads "
                + "the tree directly, so a moved or renamed project must move this boundary with "
                + "it. Continuing past a missing root would drop that project out of the gate "
                + "while the remaining roots kept the test green.");

            var found = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains(obj, StringComparison.Ordinal)
                    && !file.Contains(bin, StringComparison.Ordinal))
                {
                    found++;
                    yield return file;
                }
            }

            Assert.True(
                found > 0,
                $"Boundary root '{relative}' exists but contains no .cs file outside obj/ and "
                + "bin/. An empty root contributes nothing and reports nothing; treat it as a "
                + "moved project rather than as a clean one.");
        }
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the solution file - the same shape
    /// <c>ScenarioCoreDeclarationCensusTests</c> uses to read engine source directly.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
