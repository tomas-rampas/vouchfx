// Census gate for the dependency-env feature (spec REQ-003).
//
// REQ-003 promoted this to a MERGE GATE: T2 made `env` legal on all thirteen dependency types in a
// frozen, additive-only schema, which is correct only if every type is container-backed. `env`
// cannot be narrowed off a type inside v1.x, so a type that cannot take it would become a
// permanent no-op the schema is obliged to keep accepting. This test is the measurement.
//
// It enumerates the type list from the SCHEMA, not from a literal here, so adding a fourteenth
// dependency type without wiring the env seam turns this red rather than leaving it silently
// unmeasured.
//
// Non-Docker: Map + Configure build the resource graph in memory, and the
// EnvironmentCallbackAnnotation callbacks resolve without any live endpoint. See the test-strategy
// note at the top of EnvironmentMapperTests.cs.
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Asserts that every dependency <c>type</c> the JSON Schema accepts maps to a resource that
/// actually receives an author's <c>env:</c> mapping.
/// </summary>
public sealed class DependencyEnvCensusTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        });

    public static TheoryData<string> DependencyTypes()
    {
        var data = new TheoryData<string>();
        foreach (var t in SchemaDependencyTypeEnum())
        {
            data.Add(t);
        }

        return data;
    }

    /// <summary>
    /// Reads <c>$defs/dependency/properties/type/enum</c> out of the embedded
    /// <c>root-language-schema.json</c> — the same resource the validator composes from, so the
    /// census cannot drift from the language the schema accepts.
    /// </summary>
    private static List<string> SchemaDependencyTypeEnum()
    {
        var assembly = typeof(Vouchfx.Engine.Compilation.Schema.SchemaComposer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("root-language-schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var doc = JsonDocument.Parse(stream);
        var names = doc.RootElement
            .GetProperty("$defs").GetProperty("dependency")
            .GetProperty("properties").GetProperty("type")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
        Assert.NotEmpty(names);
        return names;
    }

    [Theory]
    [MemberData(nameof(DependencyTypes))]
    public async Task EveryDependencyType_AppliesAuthorEnvToItsOwnContainer(string type)
    {
        var probeValue = "probe-" + type;
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["dep"] = new DependencySpec(type, null, ExtraFor(type))
                {
                    Env = new Dictionary<string, string> { ["VOUCHFX_CENSUS_PROBE"] = probeValue },
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        // Exactly one container carries the declared name: the two sidecar-bearing types name
        // theirs '<dep>-sr' (kafka schema registry) and '<dep>-sqledge' (azureservicebus SQL), so
        // Single() also pins that neither is ever mistaken for the dependency itself.
        var target = Assert.Single(
            builder.Resources.OfType<ContainerResource>(), r => r.Name == "dep");

        var vars = await ResolveEnvVarsAsync(target);
        Assert.True(
            vars.ContainsKey("VOUCHFX_CENSUS_PROBE"),
            $"Dependency type '{type}' did not receive the author's env: variable. "
            + $"Resolved keys: {string.Join(", ", vars.Keys)}");
        Assert.Equal(probeValue, ValueTextOf(vars["VOUCHFX_CENSUS_PROBE"]));

        // The author configured the dependency, not its internal sidecars — those carry names no
        // author can write, so a variable landing on one would be an engine-authored surprise.
        foreach (var sidecar in builder.Resources.Where(r => r.Name != "dep"))
        {
            var sidecarVars = await ResolveEnvVarsAsync(sidecar);
            Assert.False(
                sidecarVars.ContainsKey("VOUCHFX_CENSUS_PROBE"),
                $"Dependency type '{type}': the author's env: variable leaked onto "
                + $"'{sidecar.Name}', which is not the declared dependency's own resource.");
        }
    }

    /// <summary>
    /// kafka provisions its schema-registry sidecar only when <c>schemaRegistry: true</c>, and the
    /// sidecar is the case worth covering — so the kafka row of the census opts into it.
    /// </summary>
    private static YamlMappingNode? ExtraFor(string type) =>
        type == "kafka"
            ? new YamlMappingNode
            {
                { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
            }
            : null;

    private static string ValueTextOf(object envVarValue) => envVarValue switch
    {
        ReferenceExpression re => re.ValueExpression,
        string s => s,
        _ => envVarValue.ToString() ?? string.Empty,
    };

    // ── REQ-004 / EDGE-007: the reserved set cannot silently fall behind the engine ───────────

    /// <summary>
    /// Every environment variable the engine sets on an AUTHOR-ADDRESSABLE dependency resource is
    /// declared in <c>EnvironmentMapper.s_engineSetEnvKeys</c> under that dependency's
    /// <c>type:</c>, and every name declared there is still set by that type's registration.
    /// Both directions — and any literal the census cannot confidently attribute FAILS rather
    /// than being skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the edge that decides whether REQ-004 rots.</b>  The refusal is only as good as
    /// its last update: add a <c>WithEnvironment("SOMETHING", …)</c> to a dependency's own
    /// container without joining the table and the author may override it, silently, with Aspire's
    /// last-write-wins semantics deciding the outcome — and nothing else in the repo would notice.
    /// Demonstrated red rather than assumed: adding
    /// <c>.WithEnvironment("VOUCHFX_CENSUS_CANARY", "x")</c> to the elasticsearch registration
    /// fails this test naming that variable, and removing it restores green.
    /// </para>
    /// <para>
    /// <b>A census that fails OPEN defeats its own purpose</b>, so every step below is arranged to
    /// go red on doubt.  Three earlier fail-open holes are closed by construction:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Attribution follows the RECEIVER CHAIN, not source proximity.</b>  The previous
    ///     census attributed each <c>WithEnvironment("X"</c> to the nearest preceding
    ///     <c>.Add…(</c> in the text, which is correct only by luck of ordering: registering a
    ///     sidecar — or any intervening <c>collection.Add(item)</c> — between a registration and
    ///     its own <c>WithEnvironment</c> silently moved the variable onto a resource nothing
    ///     reserves, and the census went green.  <see cref="ResolveExpression"/> instead walks the
    ///     expression the call is actually invoked on: the statement it sits in, through
    ///     <c>=</c> to the assigned expression, through the builder pass-throughs in
    ///     <see cref="s_builderPassThroughs"/>, and — when the chain head is a local — back to
    ///     that local's own assignment.  Statements between the registration and the write are
    ///     invisible to it, which is precisely the property the old census lacked.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Registration blocks are bounded by brace matching.</b>  Running the LAST block to
    ///     end-of-file (as the previous splitter did) swept every later <c>WithEnvironment</c> in
    ///     the file into <c>minio</c>'s block, where it was harmless only because
    ///     <c>ApplyEnv</c>'s call happens to pass an identifier rather than a literal.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The scan covers every <c>src/**/*.cs</c> file</b>, not just
    ///     <c>EnvironmentMapper.cs</c>.  A <c>WithEnvironment</c> literal anywhere else in the
    ///     engine cannot be attributed to a dependency registration, so it fails — naming file,
    ///     line and literal — rather than being invisible.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Why the source text and not the built graph.</b>  Resolving the callbacks on a built
    /// resource cannot separate what THE ENGINE sets from what Aspire's own <c>AddPostgres</c> /
    /// <c>AddRedis</c> / <c>AddSqlServer</c> set internally — and decision 4 puts the Aspire-set
    /// variables deliberately out of the guard's scope, so a runtime census would demand the table
    /// list names REQ-004 must not refuse.  The reserved set covers what this source writes, so
    /// this source is what the census reads.
    /// </para>
    /// <para>
    /// <b>Author-addressable</b> means the resource named exactly the declared dependency name —
    /// a registration call whose first argument is the <c>name</c> parameter itself.  The kafka
    /// schema-registry and azureservicebus SQL sidecars receive engine-set variables too, but they
    /// carry names no author can write and a dependency's <c>env:</c> never reaches them; they are
    /// excluded through the short, explicit <see cref="s_sidecarNameArguments"/> allow-list, so
    /// introducing a THIRD sidecar is a deliberate edit here rather than something the census
    /// absorbs in silence.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryEngineSetVariableOnAnAuthorAddressableResource_IsInTheReservedSet()
    {
        var reserved = ReservedEnvKeys();
        var schemaTypes = SchemaDependencyTypeEnum();
        var failures = new List<string>();

        var mapperPath = EnvironmentMapperSourcePath();
        var views = EngineSourceViews(mapperPath);
        var mapper = views.Single(
            v => string.Equals(v.Path, mapperPath, StringComparison.OrdinalIgnoreCase));

        var blocks = RegistrationBlocks(mapper);

        // A block split that missed a registration would silently census fewer types than the
        // language has, so it is measured against the schema rather than trusted.
        Assert.Equal(schemaTypes.Count, blocks.Count);

        var blockTypes = blocks.Select(b => b.Type).ToList();
        foreach (var type in schemaTypes)
        {
            Assert.Contains(type, blockTypes);
        }

        foreach (var type in reserved.Keys)
        {
            Assert.Contains(type, schemaTypes);
        }

        var engineSetByType = blocks.ToDictionary(
            b => b.Type,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var view in views)
        {
            foreach (var write in EnvironmentWrites(view))
            {
                ClassifyWrite(view, mapper, write, blocks, engineSetByType, failures);
            }
        }

        foreach (var type in engineSetByType.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            foreach (var variable in engineSetByType[type].OrderBy(v => v, StringComparer.Ordinal))
            {
                if (reserved.TryGetValue(type, out var forType) && forType.Contains(variable))
                    continue;

                failures.Add(
                    $"EnvironmentMapper sets '{variable}' on the '{type}' dependency's own "
                    + "container, but that name is not in s_engineSetEnvKeys for that type. An "
                    + "author's env: entry of that name would be applied and would win "
                    + "(Aspire is last-write-wins), silently replacing the engine's value. Add "
                    + "it to the reserved set, or move the variable onto a resource the author "
                    + "cannot name.");
            }
        }

        foreach (var type in reserved.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            foreach (var variable in reserved[type].OrderBy(v => v, StringComparer.Ordinal))
            {
                if (engineSetByType.TryGetValue(type, out var set) && set.Contains(variable))
                    continue;

                failures.Add(
                    $"s_engineSetEnvKeys reserves '{variable}' for type '{type}', but the "
                    + "mapper no longer sets it on that dependency's own container. A "
                    + "reserved name the engine does not set refuses an author's entry for "
                    + "no reason. Remove it from the table.");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"The dependency-env census (REQ-004 / EDGE-007) found {failures.Count} problem(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    /// <summary>
    /// Files each discovered <c>WithEnvironment</c> call into exactly one of: an engine-set name
    /// on an author-addressable resource (recorded against its dependency <c>type:</c>), a write
    /// onto an allow-listed sidecar (excluded), or a FAILURE.  There is no fourth, silent bucket.
    /// </summary>
    private static void ClassifyWrite(
        SourceView view,
        SourceView mapper,
        EnvWrite write,
        List<RegistrationBlock> blocks,
        Dictionary<string, HashSet<string>> engineSetByType,
        List<string> failures)
    {
        var where = $"{view.Display}({write.Line})";

        switch (write.Kind)
        {
            case EnvKeyKind.InsideStringLiteral:
                failures.Add(
                    $"{where}: a 'WithEnvironment(' call appears inside a STRING LITERAL "
                    + $"(near: {write.Text}). Source the engine generates rather than compiles is "
                    + "outside what this census can attribute — if that is deliberate, the census "
                    + "needs extending before it lands.");
                return;

            case EnvKeyKind.Dynamic:
                if (s_dynamicEnvKeyArguments.Contains(write.Text))
                    return;

                failures.Add(
                    $"{where}: WithEnvironment's first argument is the identifier "
                    + $"'{write.Text}', whose value the census cannot read. Only "
                    + $"{FormatAllowList(s_dynamicEnvKeyArguments)} is allowed to name a variable "
                    + "dynamically (ApplyEnv, which writes the AUTHOR's own keys). Use a string "
                    + "literal, or extend that allow-list deliberately.");
                return;

            case EnvKeyKind.Other:
                failures.Add(
                    $"{where}: WithEnvironment's first argument ({write.Text}) is neither a plain "
                    + "string literal nor a bare identifier, so the census cannot tell which "
                    + "variable it names.");
                return;

            default:
                break;
        }

        var block = ReferenceEquals(view, mapper)
            ? blocks.FirstOrDefault(b => write.Index >= b.Start && write.Index < b.End)
            : null;

        if (block is null)
        {
            failures.Add(
                $"{where}: the engine sets '{write.Text}' here, outside every dependency "
                + $"registration in {MapperFileName}. The census cannot tell whether that "
                + "resource is one an author can name in `dependencies:` — and a name it cannot "
                + "classify is exactly the collision REQ-004 exists to refuse. Move the write "
                + "into a registration, or extend this census to cover the new site.");
            return;
        }

        if (write.DotIndex < 0)
        {
            failures.Add(
                $"{where}: '{write.Text}' is set by a WithEnvironment call with no receiver "
                + "expression to attribute it to.");
            return;
        }

        var (owner, detail) = ResolveReceiver(view, write.DotIndex, block, 0);

        switch (owner)
        {
            case ResourceOwner.AuthorAddressable:
                engineSetByType[block.Type].Add(write.Text);
                break;

            case ResourceOwner.Sidecar:
                break;

            default:
                failures.Add(
                    $"{where}: the engine sets '{write.Text}' inside the '{block.Type}' "
                    + $"registration, but the census cannot attribute it to a resource — {detail}. "
                    + "An unattributable write is treated as a failure, not a skip: if it lands on "
                    + "the dependency's own container it is a silent collision with the author's "
                    + "env:, and the census has no way to rule that out.");
                break;
        }
    }

    // ── the census's source model ────────────────────────────────────────────────────────────
    //
    // Everything below is deliberately arranged so that "the census is unsure" and "the census
    // fails" are the same outcome.  No branch returns "ignore" except the two explicit
    // allow-lists (sidecar names, dynamic env keys) and text the masker positively identified as
    // a comment.

    private const string MapperFileName = "EnvironmentMapper.cs";

    /// <summary>The registration parameter that names the AUTHOR-ADDRESSABLE resource.</summary>
    private const string AuthorAddressableArgument = "name";

    /// <summary>
    /// The identifiers a registration may pass to a resource-registering call for a SIDECAR — a
    /// container whose name (<c>&lt;dep&gt;-sr</c>, <c>&lt;dep&gt;-sqledge</c>) no author can
    /// write, so an engine-set variable on it can never collide with a dependency's <c>env:</c>.
    /// Deliberately explicit and short: a third sidecar joins this list as a decision somebody
    /// makes, not as a side effect the census silently accepts.
    /// </summary>
    private static readonly HashSet<string> s_sidecarNameArguments =
        new(StringComparer.Ordinal) { "srName", "sidecarName" };

    /// <summary>
    /// Helpers that take a resource builder as their FIRST argument and hand it back, so a
    /// receiver chain resolves THROUGH them to the registration underneath.  Any other call at the
    /// head of a chain is unattributable and fails — the census never assumes an unknown helper is
    /// transparent.
    /// </summary>
    private static readonly HashSet<string> s_builderPassThroughs =
        new(StringComparer.Ordinal) { "ApplyImageOverrides", "ApplySidecarRegistryAndPullPolicy" };

    /// <summary>
    /// The only identifiers allowed to name an environment variable dynamically: <c>ApplyEnv</c>'s
    /// loop variable, which writes the AUTHOR's own keys (already cleared by <c>Map</c>'s eager
    /// refusal) rather than an engine-set one.  Every other non-literal name fails, because a
    /// constant held in a field or a computed name would otherwise slip past unmeasured.
    /// </summary>
    private static readonly HashSet<string> s_dynamicEnvKeyArguments =
        new(StringComparer.Ordinal) { "key" };

    private enum EnvKeyKind
    {
        Literal,
        Dynamic,
        Other,
        InsideStringLiteral,
    }

    private enum ResourceOwner
    {
        AuthorAddressable,
        Sidecar,
        Unresolved,
    }

    /// <summary>One <c>WithEnvironment(</c> call site found in an engine source file.</summary>
    private sealed record EnvWrite(int Index, int DotIndex, int Line, EnvKeyKind Kind, string Text);

    /// <summary>
    /// One <c>["type"] = new DependencyRegistration(…)</c> entry, bounded by BRACE MATCHING over
    /// its argument list.  Bounding the last entry at the next entry's start — or, worse, at
    /// end-of-file — is what let unrelated code downstream be censused as part of it.
    /// </summary>
    private sealed record RegistrationBlock(string Type, int Start, int End);

    /// <summary>
    /// A C# source file plus two parallel views of it, all sharing one index space:
    /// <see cref="Text"/> verbatim, <see cref="Skeleton"/> with comment text and string CONTENTS
    /// blanked to spaces (so brace/paren matching and identifier scanning cannot be derailed by a
    /// <c>//</c> inside a URL or a <c>)</c> inside a message), and <see cref="Kind"/> recording
    /// per character whether it was code (<c>c</c>), comment (<c>/</c>) or string content
    /// (<c>s</c>).
    /// </summary>
    private sealed class SourceView
    {
        public SourceView(string path, string display, string text, string skeleton, string kind)
        {
            Path = path;
            Display = display;
            Text = text;
            Skeleton = skeleton;
            Kind = kind;
        }

        public string Path { get; }

        public string Display { get; }

        public string Text { get; }

        public string Skeleton { get; }

        public string Kind { get; }
    }

    /// <summary>
    /// Every <c>src/**/*.cs</c> file that mentions <c>WithEnvironment</c> at all, masked.  The
    /// breadth is the point: the reserved set is engine-wide, so a census that reads only
    /// <c>EnvironmentMapper.cs</c> is sound only for as long as nobody writes a container
    /// environment variable anywhere else — a property nothing enforced.
    /// </summary>
    private static List<SourceView> EngineSourceViews(string mapperPath)
    {
        var root = FindRepoRoot();
        var srcRoot = Path.Combine(root, "src");
        Assert.True(
            Directory.Exists(srcRoot),
            $"'{srcRoot}' is missing; this census reads every engine source file beneath it.");

        var views = new List<SourceView>();
        foreach (var file in Directory
                     .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (IsBuildOutput(file))
                continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("WithEnvironment", StringComparison.Ordinal))
                continue;

            var display = Path.GetRelativePath(root, file).Replace('\\', '/');
            views.Add(Mask(file, display, text));
        }

        Assert.Contains(
            views,
            v => string.Equals(v.Path, mapperPath, StringComparison.OrdinalIgnoreCase));
        return views;
    }

    private static bool IsBuildOutput(string file)
    {
        var sep = Path.DirectorySeparatorChar;
        return file.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a <see cref="SourceView"/>: a single left-to-right pass that recognises line and
    /// block comments, character literals, and regular / verbatim / interpolated / raw string
    /// literals, blanking their interiors while preserving every index and newline.
    /// </summary>
    private static SourceView Mask(string path, string display, string text)
    {
        var skeleton = text.ToCharArray();
        var kind = new char[text.Length];
        Array.Fill(kind, 'c');

        void Blank(int from, int to, char marker)
        {
            for (var i = Math.Max(0, from); i < to && i < text.Length; i++)
            {
                kind[i] = marker;
                if (skeleton[i] != '\n')
                    skeleton[i] = ' ';
            }
        }

        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];

            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var newline = text.IndexOf('\n', index);
                var end = newline < 0 ? text.Length : newline;
                Blank(index, end, '/');
                index = end;
                continue;
            }

            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var closing = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var end = closing < 0 ? text.Length : closing + 2;
                Blank(index, end, '/');
                index = end;
                continue;
            }

            if (c == '\'')
            {
                var j = index + 1;
                while (j < text.Length && text[j] != '\'')
                    j += text[j] == '\\' ? 2 : 1;

                Blank(index + 1, Math.Min(j, text.Length), 's');
                index = Math.Min(j + 1, text.Length);
                continue;
            }

            if (c == '"')
            {
                var quotes = 0;
                while (index + quotes < text.Length && text[index + quotes] == '"')
                    quotes++;

                var verbatim = index > 0 && (text[index - 1] == '@'
                    || (index > 1 && text[index - 1] == '$' && text[index - 2] == '@'));

                int contentStart;
                int contentEnd;
                int next;

                if (quotes >= 3)
                {
                    contentStart = index + quotes;
                    var terminator = new string('"', quotes);
                    var closing = text.IndexOf(terminator, contentStart, StringComparison.Ordinal);
                    contentEnd = closing < 0 ? text.Length : closing;
                    next = closing < 0 ? text.Length : closing + quotes;
                }
                else if (verbatim)
                {
                    var j = index + 1;
                    while (j < text.Length)
                    {
                        if (text[j] != '"')
                        {
                            j++;
                            continue;
                        }

                        if (j + 1 < text.Length && text[j + 1] == '"')
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    contentStart = index + 1;
                    contentEnd = Math.Min(j, text.Length);
                    next = Math.Min(j + 1, text.Length);
                }
                else
                {
                    var j = index + 1;
                    while (j < text.Length && text[j] != '"')
                        j += text[j] == '\\' ? 2 : 1;

                    contentStart = index + 1;
                    contentEnd = Math.Min(j, text.Length);
                    next = Math.Min(j + 1, text.Length);
                }

                Blank(contentStart, contentEnd, 's');
                index = next;
                continue;
            }

            index++;
        }

        return new SourceView(path, display, text, new string(skeleton), new string(kind));
    }

    /// <summary>
    /// Every <c>WithEnvironment(</c> occurrence in a file, classified by what its FIRST argument
    /// is.  Discovery runs over the verbatim text — a masker bug must never be able to HIDE a call
    /// — and the mask is then consulted to decide whether the occurrence is real code (analysed),
    /// prose in a comment (ignored) or text inside a string literal (a failure).
    /// </summary>
    private static List<EnvWrite> EnvironmentWrites(SourceView view)
    {
        var writes = new List<EnvWrite>();

        foreach (Match m in Regex.Matches(view.Text, @"(?<![A-Za-z0-9_])WithEnvironment\s*\("))
        {
            var marker = view.Kind[m.Index];
            if (marker == '/')
                continue;

            var line = LineOf(view, m.Index);

            if (marker == 's')
            {
                var excerpt = view.Text[m.Index..Math.Min(view.Text.Length, m.Index + 60)];
                writes.Add(new EnvWrite(
                    m.Index, -1, line, EnvKeyKind.InsideStringLiteral, CollapseWhitespace(excerpt)));
                continue;
            }

            var open = m.Index + m.Length - 1;
            var (kind, keyText) = FirstEnvKey(view, open);
            writes.Add(new EnvWrite(m.Index, DotBefore(view, m.Index), line, kind, keyText));
        }

        return writes;
    }

    private static (EnvKeyKind Kind, string Text) FirstEnvKey(SourceView view, int openParen)
    {
        var skeleton = view.Skeleton;
        var j = SkipWhitespace(skeleton, openParen + 1);
        if (j >= skeleton.Length)
            return (EnvKeyKind.Other, "<end of file>");

        if (skeleton[j] == '"')
        {
            var prefixed = j > 0 && (view.Text[j - 1] == '@' || view.Text[j - 1] == '$');
            var raw = j + 2 < skeleton.Length
                && skeleton[j + 1] == '"'
                && skeleton[j + 2] == '"';

            if (prefixed || raw)
                return (EnvKeyKind.Other, "an interpolated, verbatim or raw string literal");

            var close = skeleton.IndexOf('"', j + 1);
            if (close < 0)
                return (EnvKeyKind.Other, "an unterminated string literal");

            return (EnvKeyKind.Literal, view.Text[(j + 1)..close]);
        }

        var (end, identifier) = ReadIdentifier(skeleton, j);
        if (identifier.Length > 0)
        {
            var after = SkipWhitespace(skeleton, end);
            if (after < skeleton.Length && (skeleton[after] == ',' || skeleton[after] == ')'))
                return (EnvKeyKind.Dynamic, identifier);
        }

        var slice = view.Text[j..Math.Min(view.Text.Length, j + 60)];
        return (EnvKeyKind.Other, CollapseWhitespace(slice));
    }

    /// <summary>
    /// The <c>["type"] = new DependencyRegistration(</c> entries, each bounded at the parenthesis
    /// that closes its own argument list.
    /// </summary>
    private static List<RegistrationBlock> RegistrationBlocks(SourceView view)
    {
        var blocks = new List<RegistrationBlock>();

        foreach (Match m in Regex.Matches(
            view.Text, @"\[""(?<type>[a-z]+)""\]\s*=\s*new DependencyRegistration\s*\("))
        {
            if (view.Kind[m.Index] != 'c')
                continue;

            var open = m.Index + m.Length - 1;
            var close = MatchingClose(view.Skeleton, open);
            Assert.True(
                close > open,
                $"{view.Display}({LineOf(view, m.Index)}): the '{m.Groups["type"].Value}' "
                + "registration's argument list is not balanced, so the census cannot bound it.");

            blocks.Add(new RegistrationBlock(m.Groups["type"].Value, m.Index, close + 1));
        }

        return blocks;
    }

    // ── receiver-chain resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves which resource a <c>WithEnvironment</c> call writes to, by following the
    /// expression it is invoked ON.  Proximity in the source text plays no part.
    /// </summary>
    private static (ResourceOwner Kind, string Detail) ResolveReceiver(
        SourceView view, int dotIndex, RegistrationBlock block, int depth)
    {
        if (depth > MaxResolutionDepth)
            return (ResourceOwner.Unresolved, "the receiver chain nests deeper than this census resolves");

        var statementStart = StatementStart(view, dotIndex, block.Start);
        var expressionStart = AfterTopLevelAssignment(view, statementStart, dotIndex);
        return ResolveExpression(view, expressionStart, dotIndex, block, depth);
    }

    private const int MaxResolutionDepth = 6;

    /// <summary>
    /// Walks the expression starting at <paramref name="start"/> up to
    /// <paramref name="stopIndex"/>, returning the resource its LAST registration call names.
    /// A chain head that is a call resolves through <see cref="s_builderPassThroughs"/>; a chain
    /// head that is a bare local resolves through that local's own assignment.
    /// </summary>
    private static (ResourceOwner Kind, string Detail) ResolveExpression(
        SourceView view, int start, int stopIndex, RegistrationBlock block, int depth)
    {
        if (depth > MaxResolutionDepth)
            return (ResourceOwner.Unresolved, "the receiver chain nests deeper than this census resolves");

        var skeleton = view.Skeleton;
        var headStart = SkipWhitespace(skeleton, start);
        var (headEnd, head) = ReadIdentifier(skeleton, headStart);
        if (head.Length == 0)
            return (ResourceOwner.Unresolved, "the receiver chain does not begin with an identifier");

        (ResourceOwner Kind, string Detail)? owner = null;
        var position = SkipWhitespace(skeleton, headEnd);

        if (position < skeleton.Length && skeleton[position] == '(')
        {
            var close = MatchingClose(skeleton, position);
            if (close < 0)
                return (ResourceOwner.Unresolved, "the receiver chain has unbalanced parentheses");

            if (head.StartsWith("Add", StringComparison.Ordinal))
            {
                owner = ClassifyRegistrationArgument(view, position, close, head);
            }
            else if (s_builderPassThroughs.Contains(head))
            {
                owner = ResolveExpression(
                    view, position + 1, Math.Min(stopIndex, close), block, depth + 1);
            }
            else
            {
                return (
                    ResourceOwner.Unresolved,
                    $"the chain starts at '{head}(…)', which the census does not know to return "
                    + "the resource builder its first argument names; add it to "
                    + "s_builderPassThroughs if it does");
            }

            position = close + 1;
        }

        while (true)
        {
            position = SkipWhitespace(skeleton, position);
            if (position >= stopIndex || position >= skeleton.Length || skeleton[position] != '.')
                break;

            var namePosition = SkipWhitespace(skeleton, position + 1);
            var (nameEnd, method) = ReadIdentifier(skeleton, namePosition);
            if (method.Length == 0)
                break;

            var afterName = SkipWhitespace(skeleton, nameEnd);
            if (afterName < skeleton.Length && skeleton[afterName] == '(')
            {
                var close = MatchingClose(skeleton, afterName);
                if (close < 0)
                    return (ResourceOwner.Unresolved, "the receiver chain has unbalanced parentheses");

                if (method.StartsWith("Add", StringComparison.Ordinal))
                    owner = ClassifyRegistrationArgument(view, afterName, close, method);

                position = close + 1;
            }
            else
            {
                position = nameEnd;
            }
        }

        return owner ?? ResolveLocal(view, head, headStart, block, depth + 1);
    }

    /// <summary>
    /// Resolves a local used as a receiver by finding its assignment INSIDE the same registration
    /// block and resolving that expression instead.  A self-assignment
    /// (<c>b = b.WithEnvironment(…)</c>) is stepped over, not followed, so resolution continues
    /// back to the call that actually registered the resource.
    /// </summary>
    private static (ResourceOwner Kind, string Detail) ResolveLocal(
        SourceView view, string identifier, int before, RegistrationBlock block, int depth)
    {
        if (depth > MaxResolutionDepth)
            return (ResourceOwner.Unresolved, $"resolving the local '{identifier}' exceeded the census's depth limit");

        var skeleton = view.Skeleton;
        var region = skeleton[block.Start..Math.Max(block.Start, before)];
        var assignments = Regex
            .Matches(region, $@"(?<![A-Za-z0-9_.]){Regex.Escape(identifier)}\s*=(?![=>])")
            .OrderByDescending(m => m.Index);

        foreach (var assignment in assignments)
        {
            var rhsStart = block.Start + assignment.Index + assignment.Length;
            var (_, rhsHead) = ReadIdentifier(skeleton, SkipWhitespace(skeleton, rhsStart));
            if (string.Equals(rhsHead, identifier, StringComparison.Ordinal))
                continue;

            return ResolveExpression(
                view, rhsStart, EndOfStatement(skeleton, rhsStart, block.End), block, depth);
        }

        return (
            ResourceOwner.Unresolved,
            $"'{identifier}' is not assigned a resource anywhere in this registration, so the "
            + "census cannot tell which resource it denotes");
    }

    /// <summary>
    /// Classifies the resource a registration call names from its FIRST argument: the
    /// registration's own <c>name</c> parameter (author-addressable), an allow-listed sidecar
    /// identifier (excluded), or anything else — including a derived expression such as
    /// <c>name + "db"</c> — which fails.
    /// </summary>
    private static (ResourceOwner Kind, string Detail) ClassifyRegistrationArgument(
        SourceView view, int openParen, int closeParen, string method)
    {
        var (argStart, argEnd) = FirstArgument(view.Skeleton, openParen, closeParen);
        var argument = view.Skeleton[argStart..argEnd].Trim();

        if (IsIdentifier(argument))
        {
            if (string.Equals(argument, AuthorAddressableArgument, StringComparison.Ordinal))
                return (ResourceOwner.AuthorAddressable, argument);

            if (s_sidecarNameArguments.Contains(argument))
                return (ResourceOwner.Sidecar, argument);
        }

        var written = CollapseWhitespace(view.Text[argStart..argEnd]);
        return (
            ResourceOwner.Unresolved,
            $"'{method}(…)' names its resource with `{written}`, which is neither the "
            + $"registration's own '{AuthorAddressableArgument}' parameter nor one of the sidecar "
            + $"identifiers the census allows ({FormatAllowList(s_sidecarNameArguments)})");
    }

    // ── small textual primitives, all operating on the masked skeleton ────────────────────────

    private static int StatementStart(SourceView view, int before, int lowerBound)
    {
        for (var i = before - 1; i >= lowerBound; i--)
        {
            var c = view.Skeleton[i];
            if (c is ';' or '{' or '}')
                return i + 1;
        }

        return lowerBound;
    }

    private static int AfterTopLevelAssignment(SourceView view, int start, int stop)
    {
        var skeleton = view.Skeleton;
        var depth = 0;
        var result = start;

        for (var i = start; i < stop && i < skeleton.Length; i++)
        {
            var c = skeleton[i];
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == '=' && depth == 0 && IsSimpleAssignment(skeleton, i))
                result = i + 1;
        }

        return result;
    }

    private static bool IsSimpleAssignment(string skeleton, int index)
    {
        if (index + 1 < skeleton.Length && (skeleton[index + 1] == '=' || skeleton[index + 1] == '>'))
            return false;

        return index <= 0 || !"=!<>+-*/%&|^".Contains(skeleton[index - 1]);
    }

    private static int EndOfStatement(string skeleton, int from, int upperBound)
    {
        var semicolon = skeleton.IndexOf(';', from);
        return semicolon < 0 || semicolon > upperBound ? upperBound : semicolon;
    }

    private static int MatchingClose(string skeleton, int openIndex)
    {
        var open = skeleton[openIndex];
        var close = open switch
        {
            '(' => ')',
            '[' => ']',
            _ => '}',
        };

        var depth = 0;
        for (var i = openIndex; i < skeleton.Length; i++)
        {
            if (skeleton[i] == open)
            {
                depth++;
            }
            else if (skeleton[i] == close && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static (int Start, int End) FirstArgument(string skeleton, int openIndex, int closeIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < closeIndex; i++)
        {
            var c = skeleton[i];
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ',' && depth == 1)
                return (openIndex + 1, i);
        }

        return (openIndex + 1, closeIndex);
    }

    private static int SkipWhitespace(string skeleton, int index)
    {
        while (index < skeleton.Length && char.IsWhiteSpace(skeleton[index]))
            index++;

        return index;
    }

    private static (int End, string Text) ReadIdentifier(string skeleton, int start)
    {
        if (start >= skeleton.Length || !(char.IsLetter(skeleton[start]) || skeleton[start] == '_'))
            return (start, string.Empty);

        var i = start + 1;
        while (i < skeleton.Length && (char.IsLetterOrDigit(skeleton[i]) || skeleton[i] == '_'))
            i++;

        return (i, skeleton[start..i]);
    }

    private static bool IsIdentifier(string candidate) =>
        candidate.Length > 0
        && (char.IsLetter(candidate[0]) || candidate[0] == '_')
        && candidate.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static int DotBefore(SourceView view, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(view.Skeleton[i]))
            i--;

        return i >= 0 && view.Skeleton[i] == '.' ? i : -1;
    }

    private static int LineOf(SourceView view, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < view.Text.Length; i++)
        {
            if (view.Text[i] == '\n')
                line++;
        }

        return line;
    }

    private static string CollapseWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string FormatAllowList(HashSet<string> names) =>
        string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>
    /// Reads the mapper's own reserved table by reflection, so this census and the refusal it
    /// polices share ONE declaration.  Spelling the nine names again here would make the census a
    /// second source of truth, which is the drift it exists to prevent.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ReservedEnvKeys()
    {
        var field = typeof(EnvironmentMapper).GetField(
            "s_engineSetEnvKeys", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        return (IReadOnlyDictionary<string, IReadOnlySet<string>>)field!.GetValue(null)!;
    }

    private static string EnvironmentMapperSourcePath()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "src",
            "Engine",
            "Vouchfx.Engine.Orchestration",
            "EnvironmentMapper.cs");

        Assert.True(File.Exists(path), $"'{path}' is missing; this census reads it.");
        return path;
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the directory containing
    /// <c>vouchfx.sln</c>.  Mirrors <c>EventContractFreezeTests.FindRepoRoot</c>.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln'). This census must read "
            + "EnvironmentMapper.cs from the source tree.");
    }

    /// <summary>
    /// Runs every registered <see cref="EnvironmentCallbackAnnotation"/> on
    /// <paramref name="resource"/> and returns the populated environment-variables dictionary —
    /// the same in-memory technique <c>EnvironmentMapperTests.ResolveEnvVarsAsync</c> uses.
    /// </summary>
    private static async Task<Dictionary<string, object>> ResolveEnvVarsAsync(IResource resource)
    {
        var envVars = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource,
            envVars);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(callbackContext);
        }

        return envVars;
    }
}
