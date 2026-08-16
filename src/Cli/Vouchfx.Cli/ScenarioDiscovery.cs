// Vouchfx.Cli — ScenarioDiscovery (S07-C-01).
//
// Walks a directory tree for *.e2e.yaml files and parses each into a ScenarioAst.
// A single *.e2e.yaml file is also accepted as the root — `vouchfx run <file>` (the
// advertised form, and the only form --watch can meaningfully target) resolves to a
// one-element discovery result.
// Parsing lives HERE (not deferred to the runner) so that a later task can select
// scenarios by metadata (tag/owner/name) without re-reading or re-parsing the files.
//
// A parse / AST-build failure is captured as a ParseError on the DiscoveredScenario
// rather than thrown: a malformed file must not crash discovery of the rest of the
// suite. The run command surfaces such a file as an Inconclusive scenario (§12.1 — an
// authoring error, the scenario never ran), never as a Fail.
//
// A file refused by AstBuilder.Build ALSO hands back the document it bound
// (DiscoveredScenario.RecoveredDocument, issue #411): its `environment`, so a `security` block
// declared by a document that never runs still reaches the runner's canonical SecuredTargets walk
// (and its raw text with it, for the shapes that walk cannot see), and its `metadata`, so a
// `--tag`/`--owner` filter no longer silently drops the file before that walk happens.

using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

/// <summary>
/// A single <c>.e2e.yaml</c> file found under the discovery root, with its raw text and
/// (when it parses) its built <see cref="ScenarioAst"/>.
/// </summary>
/// <param name="AbsolutePath">
/// The absolute path to the file.  Captured (rather than a relative path) because later
/// selection / reporting tasks key off a stable, unambiguous identity.
/// </param>
/// <param name="YamlText">The verbatim file contents (needed for schema validation and compile).</param>
/// <param name="Ast">
/// The built scenario AST, or <see langword="null"/> when parsing failed (see
/// <paramref name="ParseError"/>).
/// </param>
/// <param name="ParseError">
/// A human-readable parse / AST-build error message, or <see langword="null"/> when the
/// file parsed cleanly.
/// </param>
internal sealed record DiscoveredScenario(
    string AbsolutePath,
    string YamlText,
    ScenarioAst? Ast,
    string? ParseError)
{
    /// <summary>
    /// <see langword="true"/> when the file failed to parse (carries a
    /// <see cref="ParseError"/> and a <see langword="null"/> <see cref="Ast"/>).
    /// </summary>
    /// <remarks>
    /// <strong>Unchanged by <see cref="RecoveredDocument"/>, deliberately.</strong> A document
    /// whose binding was recovered still failed: it has no <see cref="Ast"/>, it is never run,
    /// and it still folds into the suite verdict as Inconclusive. The recovery adds evidence about
    /// what it DECLARED and how it is LABELLED, never a claim that it is usable.
    /// </remarks>
    public bool Failed => Ast is null;

    /// <summary>
    /// The bound document of a file that <em>parsed</em> but whose AST could not be built —
    /// <see langword="null"/> for every other outcome, including success (issue #411).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ONE failure class fills this, and the other three cannot.</strong>
    /// <see cref="ScenarioDiscovery.ParseFile"/> has four ways to fail: an oversized file, an
    /// unreadable file, <c>YamlDocumentParser.Parse</c> itself throwing, and
    /// <c>AstBuilder.Build</c> throwing. Only the last of those has a BOUND DOCUMENT in hand when
    /// it fails, and that document is exactly what <c>SecuredTargets.Enumerate</c> consumes. The
    /// other three have no bound anything, so recovering a declaration there would need a raw-YAML
    /// scan for a <c>security:</c> key — a SECOND spelling of "does this document declare
    /// security" that can disagree with the canonical AST walk, which <c>SecuredTargets</c>' own
    /// header forbids. Those three classes therefore remain the residual of #411, measured rather
    /// than described by <c>SecurityAssuranceMatrixTests.Row09c_*</c>.
    /// </para>
    /// <para>
    /// <strong>The recovered TEXT is not a fourth class, and the distinction is why this member is
    /// the whole document rather than only its environment.</strong> A <c>security</c> node that is
    /// not a mapping (<c>security: mtls</c>; a bare <c>security:</c>) binds no <c>SecuritySpec</c>,
    /// so the canonical walk over this document reports nothing — and the engine's answer to THAT
    /// shape has always been the schema door, not a bespoke scan. <c>UnbuiltDocument.Assure</c>
    /// runs that same door over <see cref="YamlText"/>, which this record already carried for every
    /// outcome. It applies only to a document in THIS class: a file whose text never even parsed is
    /// not handed to the runner at all.
    /// </para>
    /// <para>
    /// <strong>Failing CLOSED on the residual is not the missing half.</strong> Treating "cannot
    /// tell whether it declared security" as "it did" would redden every unsecured suite that
    /// merely contains an unreadable file — a far larger blast radius than the hole it closes.
    /// </para>
    /// <para>
    /// This carries no disclosure that <see cref="Ast"/> does not already carry: a parsed
    /// scenario's <see cref="ScenarioAst"/> holds the same <see cref="EnvironmentSpec"/> and the
    /// same steps, so the record's generated <c>ToString()</c> could already expand a declared
    /// <c>clientKeyPassword</c>. Nothing interpolates this record; the guard that matters is
    /// downstream, where <c>SecurityAssurance</c> keeps declared target NAMES rather than the
    /// specs they came from (issue #408).
    /// </para>
    /// </remarks>
    public E2eDocument? RecoveredDocument { get; init; }

    /// <summary>
    /// The <c>environment</c> block <see cref="RecoveredDocument"/> bound, or
    /// <see langword="null"/> when nothing was recovered or the document declared none.
    /// </summary>
    public EnvironmentSpec? RecoveredEnvironment => RecoveredDocument?.Environment;

    /// <summary>
    /// The <c>metadata</c> block <see cref="RecoveredDocument"/> bound, or <see langword="null"/>
    /// when nothing was recovered or the document declared none.
    /// </summary>
    /// <remarks>
    /// <strong>Recovered for the SELECTOR, and the omission was its own false negative.</strong>
    /// <c>ScenarioSelector</c> matches <c>--tag</c>/<c>--owner</c> against
    /// <c>Ast?.Metadata</c>, and an unbuilt document has no <see cref="Ast"/> — so before this
    /// every metadata filter excluded it, selection running (in <c>RunCommand</c>) BEFORE the split
    /// that feeds the runner's unbuilt documents. Measured: a secured unbuildable file beside a
    /// sibling tagged <c>smoke</c> exited 4 under <c>vouchfx run &lt;dir&gt;</c> and 0 under
    /// <c>--tag smoke</c>, with its parse error not even printed. The metadata was bound by the
    /// same <c>Parse</c> call that bound the environment and was thrown away beside it; recovering
    /// both lets the selector answer for itself instead of failing open. A document whose recovered
    /// tags genuinely do not match stays excluded — that is the user's own instruction.
    /// </remarks>
    public MetadataSpec? RecoveredMetadata => RecoveredDocument?.Metadata;
}

/// <summary>
/// Thrown when the discovery root exists but cannot be used as one — today, an existing
/// file whose name does not end in <c>.e2e.yaml</c>.  A usage error the caller maps to
/// exit code 2 (accepting an arbitrary file would let a typo'd path parse-fail into an
/// Inconclusive verdict, which exits 0 by default — a false green in CI).
/// </summary>
internal sealed class ScenarioDiscoveryException : Exception
{
    public ScenarioDiscoveryException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Discovers and parses <c>.e2e.yaml</c> scenario files under a root directory, or a
/// single explicitly named <c>.e2e.yaml</c> file.
/// </summary>
internal static class ScenarioDiscovery
{
    /// <summary>The glob the discovery walk matches.</summary>
    public const string ScenarioGlob = "*.e2e.yaml";

    /// <summary>
    /// The maximum permitted size, in bytes, of a single <c>.e2e.yaml</c> document
    /// (issue #266 — hostile-input hardening). 1 MiB is deliberately generous: real
    /// scenario files run from a few hundred bytes to low single-digit KiB; this bound
    /// only ever rejects pathological input, guarding the file-read seam itself
    /// (independent of, and in addition to, <c>script.csharp</c>'s own body-size guard —
    /// see <c>ScriptCsharpProvider.Validate</c>).
    /// </summary>
    internal const long MaxDocumentSizeBytes = 1024 * 1024; // 1 MiB.

    /// <summary>
    /// Recursively finds every <c>*.e2e.yaml</c> file under <paramref name="root"/> and
    /// parses each into a <see cref="DiscoveredScenario"/>; a root naming a single
    /// <c>*.e2e.yaml</c> file yields exactly that scenario.
    /// </summary>
    /// <param name="root">
    /// The directory to search, or a single <c>*.e2e.yaml</c> file.  A relative path is
    /// resolved against the current working directory; <c>"."</c> means the working
    /// directory.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry used to build the AST (the AST builder needs it to
    /// resolve step types).
    /// </param>
    /// <returns>
    /// The discovered scenarios in a stable, ordinal-sorted path order (so a run is
    /// deterministic regardless of filesystem enumeration order).  A file that fails to
    /// parse is included with a non-null <see cref="DiscoveredScenario.ParseError"/>.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <paramref name="root"/> does not exist — a usage error the caller maps
    /// to exit code 2.
    /// </exception>
    /// <exception cref="ScenarioDiscoveryException">
    /// Thrown when <paramref name="root"/> is an existing file that does not end in
    /// <c>.e2e.yaml</c> — a usage error the caller maps to exit code 2.
    /// </exception>
    public static IReadOnlyList<DiscoveredScenario> Discover(string root, StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);

        var fullRoot = Path.GetFullPath(root);

        if (File.Exists(fullRoot))
        {
            // The suffix check is OrdinalIgnoreCase deliberately: a user explicitly naming
            // an existing file (Login.E2E.YAML) must not be rejected on a case technicality
            // — on Windows it literally is the file.  This is asymmetric with directory
            // enumeration, whose glob is case-sensitive on Linux: explicit naming is not
            // discovery.
            if (!fullRoot.EndsWith(".e2e.yaml", StringComparison.OrdinalIgnoreCase))
            {
                throw new ScenarioDiscoveryException(
                    $"Discovery root '{root}' is a file but not a {ScenarioGlob} scenario "
                    + $"(resolved to '{fullRoot}'). Scenario files must end in '.e2e.yaml'.");
            }

            return new[] { ParseFile(fullRoot, registry) };
        }

        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"Discovery root '{root}' does not exist (resolved to '{fullRoot}'). "
                + $"Pass a directory to search recursively for {ScenarioGlob} scenarios, "
                + "or a single *.e2e.yaml file.");
        }

        var files = Directory
            .EnumerateFiles(fullRoot, ScenarioGlob, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var results = new List<DiscoveredScenario>(files.Count);
        foreach (var path in files)
        {
            results.Add(ParseFile(path, registry));
        }

        return results;
    }

    /// <summary>
    /// Reads and parses a single scenario file, capturing any parse / AST-build failure
    /// as a <see cref="DiscoveredScenario.ParseError"/> rather than throwing.
    /// </summary>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker tests can exercise the
    /// parse-failure-capture path against a single temp file.
    /// </remarks>
    internal static DiscoveredScenario ParseFile(string absolutePath, StepKindRegistry registry)
    {
        string yamlText;
        try
        {
            // Document-size cap (issue #266 — hostile-input hardening): reject an
            // oversized file BEFORE reading its full contents into memory. Checked here,
            // at the seam nearest the raw file read, so it applies uniformly to every
            // discovered scenario regardless of its content — independent of (and in
            // addition to) script.csharp's own inline-body/file-reference size guard,
            // which only bounds ONE step's body, not the whole document.
            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > MaxDocumentSizeBytes)
            {
                return new DiscoveredScenario(absolutePath, string.Empty, Ast: null,
                    ParseError: $"File size {fileInfo.Length} bytes exceeds the "
                        + $"{MaxDocumentSizeBytes}-byte (1 MiB) limit for a single "
                        + $"{ScenarioGlob} document (a guard against pathological input); "
                        + "split the suite into smaller files.");
            }

            yamlText = File.ReadAllText(absolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file (I/O fault or access denied — the latter is NOT an
            // IOException) is captured, not thrown: discovery of the rest of the suite
            // must proceed.  The empty YAML keeps the record well-formed.  This also
            // catches a FileInfo.Length fault on an inaccessible/racy path (e.g. deleted
            // between EnumerateFiles and here), keeping that failure mode identical to an
            // unreadable file rather than a new, unhandled exception shape.
            return new DiscoveredScenario(absolutePath, string.Empty, Ast: null,
                ParseError: $"Could not read file: {ex.Message}");
        }

        // THE TWO STAGES ARE SPLIT, and the split is the whole of issue #411's fix on this side.
        //
        // They used to share one `try`. On an AstBuilder failure the BOUND DOCUMENT already existed
        // — `Parse` had returned it, `Environment.Services[].Security` and all — and the catch threw
        // it away. That document is exactly what `SecuredTargets.Enumerate` consumes, so a secured
        // document with (say) an unknown step type declared mtls into a void: dropped into
        // RunCommand's `failures` list, never reaching the runner's canonical walk, `Declared` empty,
        // nothing raised. ALONE it exited 4 through issue #278's all-parse-failure rule; beside one
        // parsing sibling that rescue does not apply and it exited 0 — mTLS declared, never
        // exercised, pipeline green.
        //
        // The two catches are otherwise IDENTICAL — same message shape, same null Ast, same
        // `Failed` — so nothing about how a failure is reported or aggregated changes. Only the
        // second one has an environment to hand back.
        E2eDocument doc;
        try
        {
            doc = YamlDocumentParser.Parse(yamlText);
        }
        catch (Exception ex)
        {
            // Failure class 3: the YAML itself is malformed, so NOTHING bound. There is no
            // declaration to recover here and deliberately no attempt to find one — the text is
            // retained (it always is) but no RecoveredDocument is set, so nothing hands this file
            // to the runner and neither the canonical walk nor the schema door is asked about it.
            // See DiscoveredScenario.RecoveredDocument for why a raw-YAML scan is refused.
            return new DiscoveredScenario(absolutePath, yamlText, Ast: null,
                ParseError: $"Parse / AST error: {ex.Message}");
        }

        try
        {
            var ast = AstBuilder.Build(doc, registry);
            return new DiscoveredScenario(absolutePath, yamlText, ast, ParseError: null);
        }
        catch (Exception ex)
        {
            // Failure class 4: the document bound and only the AST build refused it, so what it
            // declared (the environment, for the runner's security walk) and how it is labelled
            // (the metadata, for the selector) are both known and are both retained.
            return new DiscoveredScenario(absolutePath, yamlText, Ast: null,
                ParseError: $"Parse / AST error: {ex.Message}")
            {
                RecoveredDocument = doc,
            };
        }
    }
}
