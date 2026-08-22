// Census gate for the dependency-env feature (spec REQ-003, and REQ-004 / EDGE-007 below).
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
// The REQ-004 census further down reads the engine's own SOURCE, and reads it with Roslyn — the
// Microsoft.CodeAnalysis.CSharp this assembly already references and already parses with (see
// HeadlessTopologySelfHealTests). "Which resource does this WithEnvironment write to" is a
// question about C# syntax, so it is answered by the C# syntax model rather than by a scanner
// this test would have to keep correct against comments, verbatim/interpolated/raw strings and
// character literals.
//
// Non-Docker: Map + Configure build the resource graph in memory, and the
// EnvironmentCallbackAnnotation callbacks resolve without any live endpoint. See the test-strategy
// note at the top of EnvironmentMapperTests.cs.
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Vouchfx.Engine.Authoring.Model;
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
    /// <b>Both directions are asserted as set equality per type</b>, which is what makes a vacuous
    /// census unreachable.  An attribution engine that resolved nothing at all would not "pass with
    /// an empty set": every one of the nine reserved names would then be reserved-but-not-set, and
    /// the test reports nine failures.
    /// </para>
    /// <para>
    /// <b>A census that fails OPEN defeats its own purpose</b>, so every step below is arranged to
    /// go red on doubt.  Four fail-open holes are closed by construction:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Attribution follows the RECEIVER CHAIN, not source proximity.</b>  Attributing a
    ///     <c>WithEnvironment("X"</c> to the nearest preceding <c>.Add…(</c> in the text is
    ///     correct only by luck of ordering: an intervening call — a sidecar registration, or a
    ///     plain <c>collection.Add(item)</c> — moves the variable onto a resource nothing
    ///     reserves, which is a name the census would then never check against the table.
    ///     <see cref="ResolveOwner"/> instead walks the expression the call
    ///     is actually invoked on, which Roslyn hands over directly as
    ///     <c>MemberAccessExpressionSyntax.Expression</c>: through the fluent <c>With…</c> links
    ///     (Aspire's convention — each returns the builder it was called on), through the builder
    ///     pass-throughs in <see cref="s_builderPassThroughs"/>, and — when the chain head is a
    ///     local — back to that local's own assignment within the same registration.  Statements
    ///     between the registration and the write are invisible to it.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Registration blocks are bounded by SYNTAX.</b>  A block is the span of the
    ///     <c>new …(…)</c> expression assigned to <c>["type"]</c> inside
    ///     <c>s_dependencyRegistry</c>'s own initialiser — <see cref="RegistrationBlocks"/> reads
    ///     the initialiser's elements, so an entry ends exactly where its object-creation
    ///     expression ends.  No text scanning is involved, which removes two ways a splitter
    ///     loses entries: bounding the LAST entry at end-of-file (sweeping every later
    ///     <c>WithEnvironment</c> in the file into <c>minio</c>'s block), and matching
    ///     <c>= new DependencyRegistration(</c> as text, which an IDE0090 "use target-typed new"
    ///     refactor to <c>= new(</c> silently reduces to no match at all.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A dynamic variable name is exempted by CALL SITE, never by the spelling of a
    ///     local.</b>  <c>ApplyEnv</c> legitimately writes names it cannot know at compile time —
    ///     they are the AUTHOR's own keys, already cleared by <c>Map</c>'s eager refusal — so its
    ///     <c>WithEnvironment(key, …)</c> is exempt.  The exemption is granted by the enclosing
    ///     METHOD (<see cref="s_dynamicEnvKeyMethods"/>), not by the identifier being called
    ///     <c>key</c>.  An allow-list keyed on the identifier's SPELLING exempts far more than
    ///     <c>ApplyEnv</c>: a registration written as <c>foreach (var (key, value) in …) b =
    ///     b.WithEnvironment(key, value);</c> sets engine variables on an author-addressable
    ///     container and joins no reserved row, exempted by nothing but the local being called
    ///     <c>key</c>.  Inside a registration only a string literal is accepted at all, whatever
    ///     the enclosing method.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The scan covers every <c>src/**/*.cs</c> file</b>, not just
    ///     <c>EnvironmentMapper.cs</c>.  A <c>WithEnvironment</c> literal anywhere else in the
    ///     engine cannot be attributed to a dependency registration, so it fails — naming file,
    ///     line and literal — rather than being invisible.  A file that does not PARSE is a
    ///     failure too, for the same reason: an unparseable file yields no invocations at all,
    ///     which reads exactly like a clean one.
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
        var sources = EngineSources(mapperPath);
        var mapper = sources.Single(
            s => string.Equals(s.FilePath, mapperPath, StringComparison.OrdinalIgnoreCase));

        var blocks = RegistrationBlocks(mapper);
        var blockTypes = blocks.Select(b => b.Type).ToList();

        // A block split that missed a registration would silently census fewer types than the
        // language has, so it is measured against the schema rather than trusted.
        Assert.True(
            blocks.Count == schemaTypes.Count,
            $"The census located {blocks.Count} dependency registration(s) in {MapperFileName}, "
            + $"but the schema accepts {schemaTypes.Count} dependency type(s). Located: "
            + $"{Listed(blockTypes)}. Schema: {Listed(schemaTypes)}. Registrations are read as "
            + $"the `[\"<type>\"] = new …(…)` elements of {MapperFileName}'s "
            + $"'{RegistryFieldName}' initialiser; a registration written in any other shape is "
            + "censused as nothing at all, so a registration this census cannot see must fail "
            + "here rather than pass quietly with fewer types measured.");

        foreach (var type in schemaTypes)
        {
            Assert.True(
                blockTypes.Contains(type),
                $"The schema accepts dependency type '{type}', but {MapperFileName}'s "
                + $"'{RegistryFieldName}' initialiser has no `[\"{type}\"] = new …(…)` entry the "
                + $"census can find. Located: {Listed(blockTypes)}.");
        }

        foreach (var type in reserved.Keys)
        {
            Assert.True(
                schemaTypes.Contains(type),
                $"s_engineSetEnvKeys reserves names for dependency type '{type}', which the "
                + $"schema does not accept. Schema types: {Listed(schemaTypes)}.");
        }

        var engineSetByType = blocks.ToDictionary(
            b => b.Type,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var source in sources)
        {
            CensusSource(source, mapper, blocks, engineSetByType, failures);
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

    // ── the census's source model ────────────────────────────────────────────────────────────
    //
    // Everything below is deliberately arranged so that "the census is unsure" and "the census
    // fails" are the same outcome.  No branch returns "ignore" except the two explicit
    // allow-lists — sidecar name arguments, and the methods permitted to name a variable
    // dynamically — plus the calls Roslyn never surfaces at all because they are prose in a
    // comment.

    private const string MapperFileName = "EnvironmentMapper.cs";

    /// <summary>The field whose initialiser holds one entry per dependency type.</summary>
    private const string RegistryFieldName = "s_dependencyRegistry";

    /// <summary>The registration parameter that names the AUTHOR-ADDRESSABLE resource.</summary>
    private const string AuthorAddressableArgument = "name";

    /// <summary>The Aspire extension method that writes a container environment variable.</summary>
    private const string EnvironmentWriteMethod = "WithEnvironment";

    /// <summary>
    /// Bounds the receiver walk.  Not a design limit — every chain in the mapper today resolves
    /// well inside it — only a guarantee that a pathological or mutually-referential source
    /// cannot spin.
    /// </summary>
    private const int MaxResolutionDepth = 32;

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
    /// receiver chain resolves THROUGH them to the registration underneath.  A call at the head of
    /// a chain that is neither one of these, nor an <c>Add…</c> registration, nor a fluent
    /// <c>With…</c> link, is unattributable and fails — the census never assumes an unknown helper
    /// is transparent.
    /// </summary>
    private static readonly HashSet<string> s_builderPassThroughs =
        new(StringComparer.Ordinal) { "ApplyImageOverrides", "ApplySidecarRegistryAndPullPolicy" };

    /// <summary>
    /// The only METHODS whose <c>WithEnvironment</c> may name a variable with an identifier rather
    /// than a literal: <c>ApplyEnv</c>, which writes the AUTHOR's own keys (already cleared by
    /// <c>Map</c>'s eager refusal) rather than an engine-set one.  Scoped to the call site on
    /// purpose — an allow-list keyed on the identifier's SPELLING would exempt any local happening
    /// to be called <c>key</c>, anywhere under <c>src/</c>, including inside a registration.
    /// </summary>
    private static readonly HashSet<string> s_dynamicEnvKeyMethods =
        new(StringComparer.Ordinal) { "ApplyEnv" };

    private enum ResourceOwner
    {
        AuthorAddressable,
        Sidecar,
        Unresolved,
    }

    /// <summary>
    /// One <c>["type"] = new …(…)</c> entry of <see cref="RegistryFieldName"/>'s initialiser,
    /// bounded by the syntax span of its own object-creation expression.  <c>new(</c> and
    /// <c>new DependencyRegistration(</c> are the same node kind to Roslyn
    /// (<see cref="BaseObjectCreationExpressionSyntax"/>), so a target-typed-new refactor cannot
    /// make an entry invisible.
    /// </summary>
    private sealed record RegistrationBlock(string Type, BaseObjectCreationExpressionSyntax Node)
    {
        public TextSpan Span => Node.Span;
    }

    /// <summary>One parsed engine source file, with the mapping from position back to line.</summary>
    private sealed class EngineSource
    {
        public EngineSource(string filePath, string display, SyntaxTree tree)
        {
            FilePath = filePath;
            Display = display;
            Tree = tree;
            Root = tree.GetCompilationUnitRoot();
        }

        public string FilePath { get; }

        public string Display { get; }

        public SyntaxTree Tree { get; }

        public CompilationUnitSyntax Root { get; }

        public int LineOf(int position) =>
            Tree.GetLineSpan(new TextSpan(position, 0)).StartLinePosition.Line + 1;

        public string Where(SyntaxNode node) => $"{Display}({LineOf(node.SpanStart)})";
    }

    /// <summary>
    /// Every <c>src/**/*.cs</c> file that mentions <see cref="EnvironmentWriteMethod"/> at all,
    /// parsed.  The breadth is the point: the reserved set is engine-wide, so a census that reads
    /// only <c>EnvironmentMapper.cs</c> is sound only for as long as nobody writes a container
    /// environment variable anywhere else — a property nothing enforced.
    /// </summary>
    private static List<EngineSource> EngineSources(string mapperPath)
    {
        var root = FindRepoRoot();
        var srcRoot = Path.Combine(root, "src");
        Assert.True(
            Directory.Exists(srcRoot),
            $"'{srcRoot}' is missing; this census reads every engine source file beneath it.");

        // Parse at the latest language version rather than the repo's own <LangVersion>: the
        // census must keep seeing calls in a file that starts using a newer construct, and a
        // parse failure here is reported as a failure below, never as an empty file.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        var sources = new List<EngineSource>();
        foreach (var file in Directory
                     .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (IsBuildOutput(file))
                continue;

            var text = File.ReadAllText(file);
            if (!text.Contains(EnvironmentWriteMethod, StringComparison.Ordinal))
                continue;

            var display = Path.GetRelativePath(root, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: file);

            var errors = tree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(3)
                .Select(d => $"{d.Id} at line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: "
                    + d.GetMessage(CultureInfo.InvariantCulture))
                .ToList();
            Assert.True(
                errors.Count == 0,
                $"'{display}' mentions {EnvironmentWriteMethod} but does not parse as C#, so the "
                + "census would read no calls at all from it — indistinguishable from a file that "
                + $"writes no environment variables. First error(s): {string.Join("; ", errors)}");

            sources.Add(new EngineSource(file, display, tree));
        }

        Assert.Contains(
            sources,
            s => string.Equals(s.FilePath, mapperPath, StringComparison.OrdinalIgnoreCase));
        return sources;
    }

    private static bool IsBuildOutput(string file)
    {
        var sep = Path.DirectorySeparatorChar;
        return file.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>["type"] = new …(…)</c> entries of <see cref="RegistryFieldName"/>'s initialiser.
    /// An element in any other shape is not added; the count assertion at the call site is what
    /// turns that into a failure, naming both what was found and what the schema expects.
    /// </summary>
    private static List<RegistrationBlock> RegistrationBlocks(EngineSource mapper)
    {
        var declarator = mapper.Root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(d => string.Equals(
                d.Identifier.ValueText, RegistryFieldName, StringComparison.Ordinal));

        Assert.True(
            declarator?.Initializer is not null,
            $"{MapperFileName} no longer declares an initialised '{RegistryFieldName}' field. "
            + "The census reads the dependency registrations out of that initialiser; with the "
            + "field gone it would census nothing while reporting nothing wrong.");

        var initialiser = declarator!.Initializer!.Value
            .DescendantNodesAndSelf()
            .OfType<InitializerExpressionSyntax>()
            .FirstOrDefault(i =>
                i.IsKind(SyntaxKind.ObjectInitializerExpression)
                || i.IsKind(SyntaxKind.CollectionInitializerExpression));

        Assert.True(
            initialiser is not null,
            $"{MapperFileName}'s '{RegistryFieldName}' is initialised without a collection or "
            + "object initialiser, so the census cannot enumerate its dependency registrations.");

        var blocks = new List<RegistrationBlock>();
        foreach (var element in initialiser!.Expressions)
        {
            if (element is not AssignmentExpressionSyntax entry
                || !entry.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || entry.Left is not ImplicitElementAccessSyntax key
                || key.ArgumentList.Arguments.Count != 1
                || key.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax type
                || !type.IsKind(SyntaxKind.StringLiteralExpression)
                || entry.Right is not BaseObjectCreationExpressionSyntax creation)
            {
                continue;
            }

            blocks.Add(new RegistrationBlock(type.Token.ValueText, creation));
        }

        return blocks;
    }

    /// <summary>
    /// Files each <see cref="EnvironmentWriteMethod"/> call in one source file into exactly one
    /// of: an engine-set name on an author-addressable resource (recorded against its dependency
    /// <c>type:</c>), a write onto an allow-listed sidecar (excluded), an author-key write from an
    /// allow-listed method (excluded), or a FAILURE.  There is no fifth, silent bucket.
    /// </summary>
    private static void CensusSource(
        EngineSource source,
        EngineSource mapper,
        List<RegistrationBlock> blocks,
        Dictionary<string, HashSet<string>> engineSetByType,
        List<string> failures)
    {
        // Source the engine GENERATES rather than compiles is outside what a syntax model can
        // attribute — the parser sees a string, not a call — so it is reported rather than missed.
        // Comments need no such handling: Roslyn keeps them as trivia and the walks below never
        // descend into it.
        foreach (var token in source.Root.DescendantTokens())
        {
            if (!IsStringContentToken(token))
                continue;

            if (!token.ValueText.Contains(EnvironmentWriteMethod, StringComparison.Ordinal))
                continue;

            failures.Add(
                $"{source.Display}({source.LineOf(token.SpanStart)}): a "
                + $"'{EnvironmentWriteMethod}' call appears inside a STRING LITERAL. Source the "
                + "engine generates rather than compiles is outside what this census can "
                + "attribute — if that is deliberate, the census needs extending before it lands.");
        }

        foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!string.Equals(
                    InvokedMethodName(invocation), EnvironmentWriteMethod, StringComparison.Ordinal))
            {
                continue;
            }

            ClassifyWrite(source, mapper, invocation, blocks, engineSetByType, failures);
        }
    }

    private static void ClassifyWrite(
        EngineSource source,
        EngineSource mapper,
        InvocationExpressionSyntax invocation,
        List<RegistrationBlock> blocks,
        Dictionary<string, HashSet<string>> engineSetByType,
        List<string> failures)
    {
        var where = source.Where(invocation);

        var block = ReferenceEquals(source, mapper)
            ? blocks.FirstOrDefault(b => b.Span.Contains(invocation.SpanStart))
            : null;

        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            failures.Add(
                $"{where}: a '{EnvironmentWriteMethod}()' call with no arguments names no "
                + "variable, so the census cannot classify what it sets.");
            return;
        }

        if (arguments.Any(a => a.NameColon is not null))
        {
            failures.Add(
                $"{where}: this '{EnvironmentWriteMethod}(…)' call uses NAMED arguments, so the "
                + "census cannot read the variable's name from the first position. Pass the name "
                + "positionally, or extend the census deliberately.");
            return;
        }

        var key = arguments[0].Expression;

        if (block is null)
        {
            var enclosing = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            var enclosingName = enclosing?.Identifier.ValueText;

            if (key is IdentifierNameSyntax dynamicKey)
            {
                if (enclosingName is not null && s_dynamicEnvKeyMethods.Contains(enclosingName))
                    return;

                failures.Add(
                    $"{where}: {EnvironmentWriteMethod}'s first argument is the identifier "
                    + $"'{dynamicKey.Identifier.ValueText}', whose value the census cannot read, "
                    + $"and the enclosing method '{enclosingName ?? "(none)"}' is not one allowed "
                    + $"to name a variable dynamically ({Listed(s_dynamicEnvKeyMethods)} — "
                    + "ApplyEnv, which writes the AUTHOR's own keys). The exemption is scoped to "
                    + "the CALL SITE, not to the spelling of a local: a loop variable named 'key' "
                    + "elsewhere in the engine could be setting an engine variable on a container "
                    + "an author CAN name, so it is refused here. Use a string literal, or extend "
                    + "that allow-list deliberately.");
                return;
            }

            failures.Add(
                $"{where}: the engine sets {Describe(key)} here, outside every dependency "
                + $"registration in {MapperFileName}. The census cannot tell whether that "
                + "resource is one an author can name in `dependencies:` — and a name it cannot "
                + "classify is exactly the collision REQ-004 exists to refuse. Move the write "
                + "into a registration, or extend this census to cover the new site.");
            return;
        }

        if (key is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            failures.Add(
                $"{where}: this '{EnvironmentWriteMethod}(…)' sits inside the '{block.Type}' "
                + $"registration but names its variable with `{Excerpt(key)}` rather than a "
                + "string literal, so the census cannot read which variable it sets — and a "
                + "variable set on a dependency's own container without joining s_engineSetEnvKeys "
                + "is silently overridable by the author's env:. Only "
                + $"{Listed(s_dynamicEnvKeyMethods)} may name a variable dynamically, and a "
                + "dependency registration is not it. Use a string literal here.");
            return;
        }

        var variable = literal.Token.ValueText;

        if (invocation.Expression is not MemberAccessExpressionSyntax access)
        {
            failures.Add(
                $"{where}: '{variable}' is set by a {EnvironmentWriteMethod} call with no receiver "
                + "expression to attribute it to.");
            return;
        }

        var (owner, detail) = ResolveOwner(source, access.Expression, block, 0);

        switch (owner)
        {
            case ResourceOwner.AuthorAddressable:
                engineSetByType[block.Type].Add(variable);
                break;

            case ResourceOwner.Sidecar:
                break;

            default:
                failures.Add(
                    $"{where}: the engine sets '{variable}' inside the '{block.Type}' "
                    + $"registration, but the census cannot attribute it to a resource — {detail}. "
                    + "An unattributable write is treated as a failure, not a skip: if it lands on "
                    + "the dependency's own container it is a silent collision with the author's "
                    + "env:, and the census has no way to rule that out.");
                break;
        }
    }

    // ── receiver-chain resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves which resource a <see cref="EnvironmentWriteMethod"/> call writes to, by following
    /// the expression it is invoked ON.  Proximity in the source text plays no part.
    /// </summary>
    /// <remarks>
    /// Three link shapes are understood and nothing else is assumed transparent:
    /// an <c>Add…(…)</c> call NAMES a resource (its first argument), so the walk stops there;
    /// a <see cref="s_builderPassThroughs"/> helper hands back the builder it was GIVEN, so the
    /// walk continues into its first argument; and a fluent <c>With…(…)</c> hands back the builder
    /// it was CALLED ON (Aspire's own convention for <c>IResourceBuilder&lt;T&gt;</c> extensions),
    /// so the walk continues into its receiver.  Anything else — including a call such as
    /// <c>WaitFor(…)</c> that happens to behave identically — is reported unresolved rather than
    /// assumed, which is the difference between this and simply walking every <c>.</c> in the
    /// chain.
    /// </remarks>
    private static (ResourceOwner Kind, string Detail) ResolveOwner(
        EngineSource source, ExpressionSyntax receiver, RegistrationBlock block, int depth)
    {
        if (depth > MaxResolutionDepth)
            return (ResourceOwner.Unresolved, "the receiver chain nests deeper than this census resolves");

        switch (receiver)
        {
            case ParenthesizedExpressionSyntax parenthesised:
                return ResolveOwner(source, parenthesised.Expression, block, depth + 1);

            case CastExpressionSyntax cast:
                return ResolveOwner(source, cast.Expression, block, depth + 1);

            case InvocationExpressionSyntax call:
                return ResolveCall(source, call, block, depth);

            case IdentifierNameSyntax local:
                return ResolveLocal(source, local, block, depth + 1);

            default:
                return (
                    ResourceOwner.Unresolved,
                    $"the receiver `{Excerpt(receiver)}` is neither a call the census can "
                    + "attribute nor a local it can follow");
        }
    }

    private static (ResourceOwner Kind, string Detail) ResolveCall(
        EngineSource source, InvocationExpressionSyntax call, RegistrationBlock block, int depth)
    {
        var method = InvokedMethodName(call);
        if (method is null)
        {
            return (
                ResourceOwner.Unresolved,
                $"the receiver chain passes through `{Excerpt(call.Expression)}`, which the "
                + "census cannot resolve to a named method");
        }

        if (method.StartsWith("Add", StringComparison.Ordinal))
            return ClassifyRegistrationArgument(call, method);

        if (s_builderPassThroughs.Contains(method))
        {
            if (call.ArgumentList.Arguments.Count == 0)
            {
                return (
                    ResourceOwner.Unresolved,
                    $"'{method}(…)' is called with no arguments, so there is no builder for the "
                    + "census to resolve through");
            }

            return ResolveOwner(
                source, call.ArgumentList.Arguments[0].Expression, block, depth + 1);
        }

        if (call.Expression is MemberAccessExpressionSyntax fluent
            && method.StartsWith("With", StringComparison.Ordinal))
        {
            return ResolveOwner(source, fluent.Expression, block, depth + 1);
        }

        return (
            ResourceOwner.Unresolved,
            $"the chain passes through '{method}(…)', which the census does not know to return a "
            + "resource builder: add it to s_builderPassThroughs if it hands back the builder its "
            + "FIRST ARGUMENT names, or give it Aspire's fluent 'With…' name if it hands back the "
            + "builder it was called on");
    }

    /// <summary>
    /// Resolves a local used as a receiver by finding the nearest preceding assignment or
    /// declaration of it INSIDE the same registration and resolving that expression instead.  A
    /// self-assignment (<c>b = b.WithEnvironment(…)</c>) is stepped over rather than followed —
    /// its span CONTAINS the receiver being resolved — so resolution continues back to the call
    /// that actually registered the resource.
    /// </summary>
    private static (ResourceOwner Kind, string Detail) ResolveLocal(
        EngineSource source, IdentifierNameSyntax local, RegistrationBlock block, int depth)
    {
        if (depth > MaxResolutionDepth)
        {
            return (
                ResourceOwner.Unresolved,
                $"resolving the local '{local.Identifier.ValueText}' exceeded the census's depth limit");
        }

        var name = local.Identifier.ValueText;
        var position = local.SpanStart;
        var candidates = new List<(int Position, ExpressionSyntax Value)>();

        foreach (var node in block.Node.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator
                    when string.Equals(declarator.Identifier.ValueText, name, StringComparison.Ordinal)
                        && declarator.Initializer is not null:
                    Consider(declarator, declarator.Initializer.Value);
                    break;

                case AssignmentExpressionSyntax assignment
                    when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && assignment.Left is IdentifierNameSyntax target
                        && string.Equals(target.Identifier.ValueText, name, StringComparison.Ordinal):
                    Consider(assignment, assignment.Right);
                    break;

                default:
                    break;
            }
        }

        if (candidates.Count == 0)
        {
            return (
                ResourceOwner.Unresolved,
                $"'{name}' is not assigned a resource anywhere in this registration, so the "
                + "census cannot tell which resource it denotes");
        }

        var nearest = candidates.OrderByDescending(c => c.Position).First();
        return ResolveOwner(source, nearest.Value, block, depth + 1);

        void Consider(SyntaxNode site, ExpressionSyntax value)
        {
            if (site.SpanStart >= position || site.Span.Contains(position))
                return;

            candidates.Add((site.SpanStart, value));
        }
    }

    /// <summary>
    /// Classifies the resource a registration call names from its FIRST argument: the
    /// registration's own <c>name</c> parameter (author-addressable), an allow-listed sidecar
    /// identifier (excluded), or anything else — including a derived expression such as
    /// <c>name + "db"</c> — which fails.
    /// </summary>
    private static (ResourceOwner Kind, string Detail) ClassifyRegistrationArgument(
        InvocationExpressionSyntax call, string method)
    {
        var arguments = call.ArgumentList.Arguments;
        if (arguments.Count == 0)
            return (ResourceOwner.Unresolved, $"'{method}()' names no resource at all");

        if (arguments.Any(a => a.NameColon is not null))
        {
            return (
                ResourceOwner.Unresolved,
                $"'{method}(…)' uses named arguments, so the census cannot read the resource's "
                + "name from the first position");
        }

        var first = arguments[0].Expression;
        if (first is IdentifierNameSyntax id)
        {
            var identifier = id.Identifier.ValueText;

            if (string.Equals(identifier, AuthorAddressableArgument, StringComparison.Ordinal))
                return (ResourceOwner.AuthorAddressable, identifier);

            if (s_sidecarNameArguments.Contains(identifier))
                return (ResourceOwner.Sidecar, identifier);
        }

        return (
            ResourceOwner.Unresolved,
            $"'{method}(…)' names its resource with `{Excerpt(first)}`, which is neither the "
            + $"registration's own '{AuthorAddressableArgument}' parameter nor one of the sidecar "
            + $"identifiers the census allows ({Listed(s_sidecarNameArguments)})");
    }

    // ── small syntax helpers ─────────────────────────────────────────────────────────────────

    private static string? InvokedMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };

    /// <summary>
    /// True for the tokens that carry the CONTENT of a string: regular, verbatim, raw and UTF-8
    /// literals, and the text runs of an interpolated string.  Roslyn classifies all of them for
    /// free, which is the whole reason this census no longer owns a scanner.
    /// </summary>
    private static bool IsStringContentToken(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.StringLiteralToken => true,
        SyntaxKind.SingleLineRawStringLiteralToken => true,
        SyntaxKind.MultiLineRawStringLiteralToken => true,
        SyntaxKind.Utf8StringLiteralToken => true,
        SyntaxKind.Utf8SingleLineRawStringLiteralToken => true,
        SyntaxKind.Utf8MultiLineRawStringLiteralToken => true,
        SyntaxKind.InterpolatedStringTextToken => true,
        _ => false,
    };

    private static string Describe(ExpressionSyntax key) =>
        key is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? $"'{literal.Token.ValueText}'"
            : $"the variable named by `{Excerpt(key)}`";

    private static string Excerpt(SyntaxNode node)
    {
        var text = string.Join(
            " ", node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text[..79] + "…";
    }

    private static string Listed(IEnumerable<string> names) =>
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
            MapperFileName);

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
