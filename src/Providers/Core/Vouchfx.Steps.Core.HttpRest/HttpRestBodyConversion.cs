// Vouchfx.Steps.Core.HttpRest — the http.rest request-body conversion (DSL §5.1).
//
// SHARED SOURCE, COMPILED INTO TWO ASSEMBLIES. This file is part of this provider, where
// HttpRestProvider.Bind turns a step's `body:` into the template it sends, AND it is linked
// into Vouchfx.Engine.Runtime (a <Compile Include ... Link> in that project file), where
// StepTemplateCensus reads the same template to check `${secret:...}` references before any
// secret is fetched, and to derive substitution provenance and the reproducibility
// envelope. The engine must not reference a provider assembly, and this conversion must not
// become public surface on the frozen SDK, so the one implementation is shared as source:
// the census and the binder cannot disagree about what a body sends, because there is only
// one definition of it. Keep this file free of anything either assembly lacks — it may use
// only the BCL, System.Text.Json and YamlDotNet, and its doc-comment crefs may name only
// members of this file or of those libraries.
//
// It never resolves a secret and never reads one: a `${secret:source/path}` token is text
// here, and stays inside a JSON string for execution-time resolution (§17).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.HttpRest;

/// <summary>
/// Converts an <c>http.rest</c> step's <c>body:</c> into the raw template text that is
/// sent, or into the authoring refusal that explains why it cannot be.
/// </summary>
/// <remarks>
/// <para>
/// A scalar body is its literal text. A mapping or sequence body is converted to JSON
/// following YAML's meaning: merge keys are applied, quoted and block scalars are strings,
/// a plain empty value is null, an explicit core-schema tag decides the type, and an
/// untagged plain scalar is typed from its text (see <see cref="ScalarToJsonNode"/>).
/// </para>
/// <para>
/// <strong>Never throws for an authoring fault.</strong> A body that cannot be converted
/// (too deep, too large, a merge key that cannot be applied, a tag that is unsupported or
/// does not fit its value) comes back as <see cref="Result.Refusal"/>, an ASCII message
/// that names the step and, where it helps, the line. The binder carries it on the model
/// for <c>Validate</c> to report, so it is an authoring refusal rather than a provider
/// fault; the census contributes no body text for it, so the refusal is reported once, by
/// the provider. An exception that does escape is a defect in this file, and the binder
/// lets it surface as one.
/// </para>
/// </remarks>
internal static class HttpRestBodyConversion
{
    // ── Structured-body bounds (issue #346) ────────────────────────
    //
    // ToJsonNode walks an author-written structured `body:` and materialises a JsonNode
    // tree. Two shapes of input make that walk expensive without looking expensive in the
    // file: deep nesting, which recurses once per YAML level, and an
    // anchored-and-repeatedly-aliased body, which expands multiplicatively because
    // YamlDotNet's representation model shares ONE node instance across every alias site
    // while this walk re-materialises a fresh copy at each. Only the second is reachable
    // through the engine — see below, and do not let that distinction quietly go missing
    // again.
    //
    // A suite author is trusted input (script.csharp hands them arbitrary C#), so these
    // bounds are mistake-catching and robustness, NOT a security boundary. A breach is an
    // authoring refusal like every other one this file produces: it travels on the model to
    // Validate, which reports it before anything is compiled.
    //
    // WHERE EACH BOUND ACTUALLY SITS RELATIVE TO THE ENGINE — measured, because the first
    // version of this comment justified both bounds by failure modes that no engine path
    // can reach, which made the shipped author-facing messages wrong.
    //
    // The engine validates a document against the composed JSON Schema BEFORE it parses it
    // and long before it binds any step. SchemaComposer.Validate calls
    // SchemaResources.ConvertYamlToJsonDocument, whose step 2 re-serialises the object
    // graph with `new SerializerBuilder().JsonCompatible().Build()`; that serialiser's
    // MaximumRecursion default is 50. Measured on YamlDotNet 16.3.0 against exactly that
    // builder: 49 nesting levels convert cleanly, 50 throw
    // MaximumRecursionLevelReachedException. Validate wraps the call in `catch (Exception)`
    // and returns SchemaValidationResult.Invalid("Failed to parse YAML: ..."), so a body
    // deep enough to breach that ceiling ALREADY produced a clean, reported diagnostic
    // before #346. That ceiling is on the WHOLE DOCUMENT graph, of which `body` is only a
    // subtree, so the depth a body may reach is smaller still — by however many levels sit
    // above it. The direction is conservative, which is the only property the argument needs,
    // and that is why no figure for it is written here.
    // No engine path that BINDS a step can reach MaxBodyDepth at 64 — ScenarioRunner runs
    // DocumentValidator.Validate at its step 2, ahead of ProviderPipeline.Compile, and the
    // pre-topology parse paths that deliberately skip validation (EnvironmentMapper,
    // SecuredEndpointProbe) do not compile steps at all. That last sentence is read from
    // those call sites, not measured end to end; the 49/50 boundary above is measured.
    // The bound is kept as a backstop for a caller that binds without validating (the unit
    // rows do exactly that) and for the day the upstream constant moves. It is NOT the
    // thing standing between a deep document and an uncatchable StackOverflowException,
    // and no message here says it is. The upstream limit is a FIXED LIBRARY CONSTANT, not
    // a function of stack size, so the ordering between the two cannot invert on a smaller
    // stack.
    //
    // MaxBodyNodes is the bound that a real suite can still reach. The validation-time
    // conversion above carries a ceiling of its own — SchemaResources.MaxJsonChars, 16 Mi
    // characters of emitted JSON — so on the engine path a document that reaches this
    // conversion is one that conversion already survived. What the budget buys is that the
    // same document is not then paid for a second time as a much heavier JsonNode tree, and
    // that the author gets a refusal naming the step and the limit rather than a generic
    // upstream one. It does NOT make an out-of-memory condition impossible; it bounds this
    // walk, and nothing more.
    //
    // WHY A NODE BUDGET AND NOT A VISITED-NODE SET. An alias is a SHARED node, not
    // necessarily a cycle. `*defaults` under two keys is legitimate YAML that MUST expand
    // twice, and the representation model offers no way to tell that apart from the
    // amplifying case except by measuring the result. A visited set would change the
    // language's semantics — the second and later expansions of a shared node would be
    // dropped or refused — so the defence is a budget on the nodes PRODUCED, which is
    // exactly the quantity amplification blows up. A legitimate re-expansion costs its
    // own nodes and nothing more; a billion-laughs body runs the budget out.

    /// <summary>
    /// Maximum nesting depth accepted for a structured <c>body:</c>; the body node itself
    /// is depth 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Unreachable through the engine, and kept anyway as a backstop.</strong>
    /// Schema validation converts the document with a YamlDotNet serialiser whose
    /// <c>MaximumRecursion</c> default is 50 and refuses a 50-level body before parse and
    /// long before <c>HttpRestProvider.Bind</c> — measured; see the block comment above for
    /// the figures. Only a caller that binds a step WITHOUT validating the document first can
    /// reach this bound, which in this repository means the unit rows. It earns its place
    /// because it is one comparison per node and because the upstream constant is a library
    /// default this provider does not control.
    /// </para>
    /// <para>
    /// The figure is chosen against the SERIALISER, one level inside it. Measured on the
    /// pinned runtime: a 65-level <c>JsonNode</c> tree serialises and a 66-level one throws
    /// <see cref="JsonException"/>, matching the documented default of
    /// <see cref="JsonSerializerOptions.MaxDepth"/>. So 64 is one level STRICTER than what
    /// the <c>JsonSerializer.Serialize</c> call in <see cref="ToTemplate"/> could emit — a
    /// round number just inside the ceiling, not a match for it. The one body that
    /// difference refuses (exactly 65 levels) was already refused upstream at 50.
    /// </para>
    /// </remarks>
    internal const int MaxBodyDepth = 64;

    /// <summary>
    /// Maximum number of JSON nodes a structured <c>body:</c> may expand into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PICKED, not measured — there is no natural ceiling to measure against here, only a
    /// judgement about headroom. A hand-authored request body runs to tens or a few hundred
    /// nodes and a generated bulk payload to a few thousand, so 50,000 leaves between one
    /// and three orders of magnitude of room. The count is of nodes PRODUCED, so an aliased
    /// subtree is charged its full expansion at every site, which is what makes this the
    /// amplification defence rather than a size limit. A YAML merge key (<c>&lt;&lt;</c>,
    /// ZEB-67) is charged the same way: every member a merged mapping offers at a merge site
    /// costs one unit, whether or not an explicit key overrides it, on top of the nodes the
    /// members that are kept then produce.
    /// </para>
    /// <para>
    /// <strong>IT COUNTS NODES, NOT BYTES, and the difference is reachable rather than
    /// theoretical.</strong> <see cref="ScalarToJsonNode"/> wraps the scalar's existing
    /// string instance, so an aliased scalar costs ONE node per alias site however long that
    /// string is. A body that aliases one large scalar many times therefore stays well inside
    /// this budget while serialising to far more than its node count suggests. How much more
    /// is bounded by the 1 MiB document cap ScenarioDiscovery applies before reading a file,
    /// not by this constant — and that cap is the CLI's, not every caller's:
    /// <c>ProviderTestHarness.RunSingleStepAsync</c> reaches <c>Bind</c> with an in-memory
    /// string no cap applies to. This constant therefore does not cap the materialised tree
    /// at any particular size, and no claim here says it does. Widening it to a byte budget
    /// would be a separate change with its own message and its own rows.
    /// </para>
    /// </remarks>
    internal const int MaxBodyNodes = 50_000;

    /// <summary>The YAML core-schema tag of a mapping.</summary>
    private const string YamlMapTag = "tag:yaml.org,2002:map";

    /// <summary>The YAML core-schema tag of a sequence.</summary>
    private const string YamlSeqTag = "tag:yaml.org,2002:seq";

    /// <summary>The prefix every YAML core-schema tag shares; written <c>!!</c> in a document.</summary>
    private const string YamlTagPrefix = "tag:yaml.org,2002:";

    /// <summary>The outcome of converting one body.</summary>
    /// <param name="Text">
    /// The raw template that is sent: a scalar body's literal text, or a structured body's
    /// JSON. <see langword="null"/> exactly when <paramref name="Refusal"/> is set.
    /// </param>
    /// <param name="Refusal">
    /// The ASCII authoring refusal naming the step, or <see langword="null"/> when the body
    /// converted. It never quotes a body value.
    /// </param>
    internal sealed record Result(string? Text, string? Refusal);

    /// <summary>
    /// Converts <paramref name="body"/>, the value of the step's <c>body</c> key, into the
    /// template that is sent.
    /// </summary>
    /// <param name="body">The <c>body</c> value as the parser read it.</param>
    /// <param name="step">
    /// The whole step mapping; read only on the refusal path, to name the step.
    /// </param>
    /// <returns>The template text, or the refusal.</returns>
    internal static Result ToTemplate(YamlNode body, YamlMappingNode step)
    {
        // A YAML scalar body (raw string / inline JSON) is kept as its literal string: the
        // author owns the exact bytes.
        if (body is YamlScalarNode scalar)
            return new Result(scalar.Value ?? string.Empty, null);

        try
        {
            var budget = MaxBodyNodes;
            var node = ToJsonNode(body, new Merges(step), depth: 1, ref budget);
            return new Result(JsonSerializer.Serialize(node), null);
        }
        catch (RefusalException refusal)
        {
            return new Result(null, refusal.Message);
        }
    }

    /// <summary>
    /// True for a plain (unquoted) scalar. <see cref="ScalarStyle.Any"/> is how a node
    /// built in code, rather than parsed, reports an unspecified style.
    /// </summary>
    private static bool IsPlain(YamlScalarNode scalar) =>
        scalar.Style is ScalarStyle.Plain or ScalarStyle.Any;

    /// <summary>An authoring refusal raised inside the walk and returned by <see cref="ToTemplate"/>.</summary>
    /// <remarks>
    /// Private, and caught in exactly one place, so it can never escape this file: an
    /// unexpected exception type is a defect and is deliberately not caught here.
    /// </remarks>
    private sealed class RefusalException : Exception
    {
        internal RefusalException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// The merge-key state of one body's conversion, shared by every frame of the walk.
    /// </summary>
    /// <remarks>
    /// The node budget is NOT here: it is threaded through both recursive walks as a
    /// <see langword="ref"/> parameter, so merge-key resolution and the JSON walk draw on ONE
    /// allowance — that sharing is the amplification defence (issue #346). The memo is
    /// created only for a body that actually contains a merge key.
    /// </remarks>
    private sealed class Merges
    {
        private Dictionary<YamlMappingNode, List<KeyValuePair<string, YamlNode>>>? _resolved;
        private HashSet<YamlMappingNode>? _resolving;

        internal Merges(YamlMappingNode step) => Step = step;

        /// <summary>The whole step mapping; read only to name the step in a refusal.</summary>
        internal YamlMappingNode Step { get; }

        /// <summary>
        /// Mappings whose merged entries are already known, keyed by REFERENCE: an alias is
        /// the same node instance as its anchor, so a mapping merged at many sites is
        /// resolved once. <see cref="YamlNode"/> overrides equality by value, which would
        /// conflate distinct but equal mappings, hence the reference comparer.
        /// </summary>
        internal Dictionary<YamlMappingNode, List<KeyValuePair<string, YamlNode>>> Resolved =>
            _resolved ??= new Dictionary<YamlMappingNode, List<KeyValuePair<string, YamlNode>>>(
                ReferenceEqualityComparer.Instance);

        /// <summary>Mappings whose resolution is in progress; a revisit is a merge cycle.</summary>
        internal HashSet<YamlMappingNode> Resolving =>
            _resolving ??= new HashSet<YamlMappingNode>(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// Converts a structured YAML node (mapping / sequence / scalar) into a
    /// <see cref="JsonNode"/> tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scalars follow <see cref="ScalarToJsonNode"/>. Mappings honour YAML merge keys
    /// (ZEB-67) through <see cref="ResolveEntries"/>, which also fixes the member order; the
    /// <c>&lt;&lt;</c> key itself is never sent.
    /// </para>
    /// <para>
    /// The walk is BOUNDED in two independent dimensions (issue #346): nesting depth
    /// against <see cref="MaxBodyDepth"/>, and total nodes produced against
    /// <see cref="MaxBodyNodes"/>. See the constants for what each bound is and is not
    /// reachable by, and why an alias is met with a node budget rather than a visited-node
    /// set.
    /// </para>
    /// </remarks>
    /// <param name="node">The YAML node to convert.</param>
    /// <param name="merges">The merge-key memo, and the step mapping for refusals.</param>
    /// <param name="depth">Nesting depth of <paramref name="node"/>; the body node is 1.</param>
    /// <param name="budget">Nodes still allowed; shared with <see cref="ResolveEntries"/>.</param>
    private static JsonNode? ToJsonNode(YamlNode node, Merges merges, int depth, ref int budget)
    {
        if (depth > MaxBodyDepth)
            throw new RefusalException(DescribeBodyTooDeep(merges.Step, node));

        // Charge the node BEFORE materialising it: the budget bounds work still to be done,
        // not work already completed, so an amplifying alias stops AT the ceiling rather
        // than one whole subtree past it.
        if (--budget < 0)
            throw new RefusalException(DescribeBodyTooLarge(merges.Step));

        switch (node)
        {
            case YamlMappingNode map:
                {
                    RequireCollectionTag(map, YamlMapTag, merges.Step);
                    var obj = new JsonObject();
                    if (!HasMergeKey(map))
                    {
                        // No merge key: exactly the walk this conversion has always done.
                        foreach (var (k, v) in map.Children)
                            obj[KeyText(k)] = ToJsonNode(v, merges, depth + 1, ref budget);
                        return obj;
                    }

                    foreach (var (key, value) in ResolveEntries(map, merges, depth: 1, ref budget))
                        obj[key] = ToJsonNode(value, merges, depth + 1, ref budget);
                    return obj;
                }
            case YamlSequenceNode seq:
                {
                    RequireCollectionTag(seq, YamlSeqTag, merges.Step);
                    var arr = new JsonArray();
                    foreach (var item in seq.Children)
                        arr.Add(ToJsonNode(item, merges, depth + 1, ref budget));
                    return arr;
                }
            case YamlScalarNode scalar:
                return ScalarToJsonNode(scalar, merges.Step);
            default:
                return JsonValue.Create(node.ToString());
        }
    }

    /// <summary>The text of a mapping key, exactly as the walk has always read it.</summary>
    private static string KeyText(YamlNode key) =>
        key is YamlScalarNode ks ? ks.Value ?? string.Empty : key.ToString();

    /// <summary>
    /// True for a YAML merge key: the PLAIN, untagged scalar <c>&lt;&lt;</c>, or a key
    /// explicitly tagged <c>!!merge</c>.
    /// </summary>
    /// <remarks>
    /// A quoted <c>"&lt;&lt;"</c> is an ordinary key, as YAML defines it, so an author who
    /// needs a literal <c>&lt;&lt;</c> member quotes it. Same test as the compose reader's
    /// <c>ManifestInputMappings</c> applies.
    /// </remarks>
    private static bool IsMergeKey(YamlNode key) =>
        key is YamlScalarNode { Value: "<<" } scalar
        && (scalar.Tag.IsEmpty
            ? IsPlain(scalar)
            : string.Equals(scalar.Tag.Value, YamlTagPrefix + "merge", StringComparison.Ordinal));

    private static bool HasMergeKey(YamlMappingNode map)
    {
        foreach (var (key, _) in map.Children)
        {
            if (IsMergeKey(key))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the members of <paramref name="map"/> after YAML merge keys are applied, in
    /// the order they are sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The rules (YAML merge-key type).</strong> A <c>&lt;&lt;</c> key's value is a
    /// mapping or a sequence of mappings, usually aliases. Their members are merged into the
    /// containing mapping; a key the containing mapping writes explicitly wins over a merged
    /// one wherever either appears, and within a sequence an earlier mapping wins over a
    /// later one. A merged mapping's own merge keys are applied first, by the same rules. The
    /// <c>&lt;&lt;</c> key itself is not a member.
    /// </para>
    /// <para>
    /// <strong>The order (vouchfx's choice; YAML leaves mapping order unspecified).</strong>
    /// Members appear in document order of the containing mapping: an explicit key at its own
    /// position, and the merged members at the position of the <c>&lt;&lt;</c> key, in the
    /// order of the merged mapping (for a sequence, the first mapping's members, then each
    /// later mapping's members not already supplied). Members a merged mapping supplies but
    /// the containing mapping overrides are not emitted at the merge position; the explicit
    /// key keeps its own position.
    /// </para>
    /// <para>
    /// <strong>Bounded.</strong> Each mapping is resolved once per body and the result reused
    /// at every site that merges it (the memo is keyed by reference, so this is the
    /// representation model's DAG, not its expansion). Every merged member examined at a
    /// site is charged to the body's node budget, so a merge chain that amplifies runs the
    /// same budget out as an amplifying alias does. Merge chains are limited to
    /// <see cref="MaxBodyDepth"/> levels, which also bounds this recursion's stack.
    /// </para>
    /// </remarks>
    /// <param name="map">The mapping whose members are wanted.</param>
    /// <param name="merges">The merge-key memo, and the step mapping for refusals.</param>
    /// <param name="depth">Merge-chain depth of <paramref name="map"/>; a body mapping is 1.</param>
    /// <param name="budget">Nodes still allowed; shared with <see cref="ToJsonNode"/>.</param>
    private static List<KeyValuePair<string, YamlNode>> ResolveEntries(
        YamlMappingNode map,
        Merges merges,
        int depth,
        ref int budget)
    {
        if (merges.Resolved.TryGetValue(map, out var cached))
            return cached;

        if (!merges.Resolving.Add(map))
            throw new RefusalException(DescribeMergeCycle(merges.Step, map));

        try
        {
            var explicitKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, _) in map.Children)
            {
                if (!IsMergeKey(key))
                    explicitKeys.Add(KeyText(key));
            }

            var placed = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<KeyValuePair<string, YamlNode>>(map.Children.Count);
            foreach (var (key, value) in map.Children)
            {
                if (!IsMergeKey(key))
                {
                    var text = KeyText(key);
                    placed.Add(text);
                    entries.Add(new KeyValuePair<string, YamlNode>(text, value));
                    continue;
                }

                if (depth > MaxBodyDepth)
                    throw new RefusalException(DescribeMergeTooDeep(merges.Step, key));

                IEnumerable<YamlNode> sources;
                if (value is YamlSequenceNode sequence)
                {
                    RequireCollectionTag(sequence, YamlSeqTag, merges.Step);
                    sources = sequence.Children;
                }
                else
                {
                    sources = new[] { value };
                }

                foreach (var source in sources)
                {
                    if (source is not YamlMappingNode merged)
                    {
                        throw new RefusalException(
                            DescribeMergeNotMapping(merges.Step, key, value, source));
                    }

                    RequireCollectionTag(merged, YamlMapTag, merges.Step);
                    foreach (var member in ResolveEntries(merged, merges, depth + 1, ref budget))
                    {
                        if (--budget < 0)
                            throw new RefusalException(DescribeBodyTooLarge(merges.Step));
                        if (!explicitKeys.Contains(member.Key) && placed.Add(member.Key))
                            entries.Add(member);
                    }
                }
            }

            merges.Resolved.Add(map, entries);
            return entries;
        }
        finally
        {
            merges.Resolving.Remove(map);
        }
    }

    /// <summary>
    /// Refuses a mapping or sequence carrying an explicit tag other than its own core-schema
    /// tag (<c>!!map</c> / <c>!!seq</c>) or the non-specific <c>!</c>.
    /// </summary>
    private static void RequireCollectionTag(YamlNode node, string allowedTag, YamlMappingNode step)
    {
        var tag = node.Tag;
        if (tag.IsEmpty || tag.IsNonSpecific
            || string.Equals(tag.Value, allowedTag, StringComparison.Ordinal))
        {
            return;
        }

        throw new RefusalException(DescribeUnsupportedTag(step, node));
    }

    /// <summary>
    /// Names the step a refusal is about.
    /// </summary>
    /// <remarks>
    /// The step id is read from the step mapping rather than from the binding context,
    /// which is an empty marker interface in the frozen v1 contract and carries no id.
    /// <c>StepSpec.RawNode</c> is documented as the FULL step mapping including the common
    /// fields, so <c>id</c> is present on every step the engine binds. A test that binds a
    /// bare provider fragment directly has no <c>id</c> key; that case gets a neutral phrase
    /// rather than an empty quoted string.
    /// </remarks>
    private static string DescribeStep(YamlMappingNode step)
    {
        var id = step.Children.TryGetValue(new YamlScalarNode("id"), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
        return id.Length == 0 ? "an http.rest step" : "step '" + id + "'";
    }

    /// <summary>
    /// Renders the 1-based line and column of <paramref name="node"/> for a refusal.
    /// </summary>
    /// <remarks>
    /// Used by the depth message, where the offending node is a node the parser read at that
    /// position, and by the tag and merge-key messages, where the offending node is the tagged
    /// node or the <c>&lt;&lt;</c> key. For those an aliased node carries its ANCHOR's mark,
    /// which is where the tag or the merge key is actually written, so the position is the
    /// one to edit. It is deliberately NOT used by the node-budget message: YamlDotNet's
    /// representation model shares one node instance across every alias site, so the node a
    /// budget breach lands on carries the ANCHOR's mark, and printing it would point the author
    /// at the definition rather than at the <c>*alias</c> that multiplied it.
    /// </remarks>
    private static string DescribeMark(YamlNode node)
    {
        return "line "
            + node.Start.Line.ToString(CultureInfo.InvariantCulture)
            + ", column "
            + node.Start.Column.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Message for a structured <c>body:</c> that nests deeper than
    /// <see cref="MaxBodyDepth"/>.
    /// </summary>
    private static string DescribeBodyTooDeep(YamlMappingNode step, YamlNode offending)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " nests more than "
            + MaxBodyDepth.ToString(CultureInfo.InvariantCulture)
            + " levels deep (reached at "
            + DescribeMark(offending)
            + "). This is an authoring fault, not a fault in the service under test: "
            + "flatten the body, or send it as a pre-serialised JSON string, and re-run. "
            + "On the engine's own path a body this deep is refused earlier still, by "
            + "schema validation; this limit is the backstop for a caller that binds a "
            + "step without validating the document first.";
    }

    /// <summary>
    /// Message for a structured <c>body:</c> that expands past
    /// <see cref="MaxBodyNodes"/> nodes.
    /// </summary>
    private static string DescribeBodyTooLarge(YamlMappingNode step)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " expands to more than "
            + MaxBodyNodes.ToString(CultureInfo.InvariantCulture)
            + " JSON nodes. This is an authoring fault, not a fault in the service under "
            + "test. A YAML anchor expands in full at every '*alias' that refers to it, so "
            + "a few lines can describe an enormous body: check for aliases under 'body', "
            + "or split the payload across steps. The limit exists so this step refuses a "
            + "body it would otherwise materialise in full, and names the step while doing "
            + "it.";
    }

    /// <summary>
    /// Message for a merge key whose value, or one of whose sequence entries, is not a
    /// mapping.
    /// </summary>
    private static string DescribeMergeNotMapping(
        YamlMappingNode step,
        YamlNode mergeKey,
        YamlNode value,
        YamlNode offending)
    {
        var found = ReferenceEquals(value, offending)
            ? "its value is " + DescribeKind(offending)
            : "its sequence holds " + DescribeKind(offending) + " at " + DescribeMark(offending);
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " has a merge key '<<' at "
            + DescribeMark(mergeKey)
            + " that cannot be applied: "
            + found
            + ". A merge key takes a mapping, usually an alias such as '<<: *defaults', or a "
            + "sequence of mappings such as '<<: [*first, *second]'. This is an authoring "
            + "fault, not a fault in the service under test: fix the merge, or quote the key "
            + "(\"<<\") to send a member literally named '<<'.";
    }

    /// <summary>Message for a mapping that merges itself, directly or through other mappings.</summary>
    private static string DescribeMergeCycle(YamlMappingNode step, YamlMappingNode map)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " merges the mapping at "
            + DescribeMark(map)
            + " into itself through a chain of merge keys ('<<'). This is an authoring fault, "
            + "not a fault in the service under test: break the cycle and re-run.";
    }

    /// <summary>Message for a chain of merged mappings deeper than <see cref="MaxBodyDepth"/>.</summary>
    private static string DescribeMergeTooDeep(YamlMappingNode step, YamlNode mergeKey)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " chains merge keys ('<<') more than "
            + MaxBodyDepth.ToString(CultureInfo.InvariantCulture)
            + " mappings deep (reached at "
            + DescribeMark(mergeKey)
            + "). This is an authoring fault, not a fault in the service under test: "
            + "flatten the merged mappings and re-run.";
    }

    /// <summary>
    /// Message for a body node carrying a tag a request body does not support, or a
    /// collection tag on the wrong kind of node.
    /// </summary>
    private static string DescribeUnsupportedTag(YamlMappingNode step, YamlNode node)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " uses the YAML tag '"
            + DescribeTag(node.Tag)
            + "' on "
            + DescribeKind(node)
            + " at "
            + DescribeMark(node)
            + ", which a request body does not support. A body value accepts only the YAML "
            + "core-schema tags !!str, !!int, !!float, !!bool and !!null (and !!timestamp, "
            + "sent as the text written); a mapping accepts only !!map and a sequence only "
            + "!!seq. This is an authoring fault, not a fault in the service under test: "
            + "remove the tag, or quote the value to send it as a string.";
    }

    /// <summary>Message for a scalar whose content does not fit its explicit tag.</summary>
    private static string DescribeTagMismatch(YamlMappingNode step, YamlScalarNode scalar, string reason)
    {
        var tag = DescribeTag(scalar.Tag);
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " tags the value at "
            + DescribeMark(scalar)
            + " as "
            + tag
            + ", but "
            + reason
            + ". An explicit tag decides the JSON type whether or not the value is quoted. "
            + "This is an authoring fault, not a fault in the service under test: correct "
            + "the value, or remove the tag and let the value's own form decide its type.";
    }

    /// <summary>Names a node's kind for a refusal.</summary>
    private static string DescribeKind(YamlNode node) => node switch
    {
        YamlMappingNode => "a mapping",
        YamlSequenceNode => "a sequence",
        YamlScalarNode => "a scalar",
        _ => "an unresolved node",
    };

    /// <summary>
    /// Renders a tag the way an author writes it (<c>!!int</c> for a core-schema tag, the
    /// local form such as <c>!money</c> as written, <c>!&lt;…&gt;</c> otherwise), with any
    /// character outside printable ASCII escaped so the refusal stays ASCII.
    /// </summary>
    private static string DescribeTag(TagName tag)
    {
        if (tag.IsEmpty)
            return "?";

        var value = tag.Value;
        string written;
        if (value.StartsWith(YamlTagPrefix, StringComparison.Ordinal))
            written = string.Concat("!!", value.AsSpan(YamlTagPrefix.Length));
        else if (value.StartsWith('!'))
            written = value;
        else
            written = "!<" + value + ">";

        if (written.Length > 128)
            written = string.Concat(written.AsSpan(0, 128), "...");

        var sb = new System.Text.StringBuilder(written.Length);
        foreach (var ch in written)
        {
            if (ch is >= ' ' and <= '~' && ch != '\'')
                sb.Append(ch);
            else
                sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a YAML scalar in a structured body to its JSON value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, in the order they apply (ZEB-67):
    /// </para>
    /// <list type="number">
    ///   <item>An explicit tag decides the type, whatever the style: see
    ///   <see cref="TaggedScalarToJsonNode"/>.</item>
    ///   <item>A single- or double-quoted scalar is a string, so <c>"123"</c> stays a
    ///   string and so does an inline JSON fragment.</item>
    ///   <item>A literal (<c>|</c>) or folded (<c>&gt;</c>) block scalar is a string, in
    ///   every chomping form, carrying exactly the text YAML defines for it — the
    ///   representation model has already applied folding and chomping, so <c>|</c> keeps
    ///   one trailing newline, <c>|-</c> none and <c>|+</c> all of them.</item>
    ///   <item>A plain empty value (<c>note:</c>) is null, the same as <c>null</c> and
    ///   <c>~</c>, as YAML's core schema defines it.</item>
    ///   <item>Any other plain scalar is typed from its text, unchanged since S07-B-02a:
    ///   <c>true</c>/<c>false</c> (three spellings each) are booleans, <c>null</c>/<c>~</c>
    ///   are null, and text that parses as a decimal integer or a FINITE floating-point
    ///   number is a number (<c>007</c> is 7, <c>1.50</c> is 1.5, <c>1e3</c> is 1000).
    ///   Everything else, including <c>0x1F</c>, <c>.inf</c>, <c>yes</c>, every
    ///   <c>{placeholder}</c> or <c>${secret:...}</c> token, and the texts .NET would parse
    ///   as a non-finite double but JSON cannot carry (<c>NaN</c>, <c>Infinity</c>,
    ///   <c>-Infinity</c>, in any letter case, and a value such as <c>1e400</c> that
    ///   overflows a double), is a string.</item>
    /// </list>
    /// </remarks>
    private static JsonValue? ScalarToJsonNode(YamlScalarNode scalar, YamlMappingNode step)
    {
        var value = scalar.Value ?? string.Empty;

        if (!scalar.Tag.IsEmpty)
            return TaggedScalarToJsonNode(scalar, value, step);

        // A quoted scalar is always a string — honour the author's intent (e.g. "123" stays
        // a string, an inline JSON fragment stays a string). A block scalar is the same:
        // YAML gives '|' and '>' no implicit typing, so '|' holding 123 is the text "123\n".
        if (scalar.Style is ScalarStyle.SingleQuoted
            or ScalarStyle.DoubleQuoted
            or ScalarStyle.Literal
            or ScalarStyle.Folded)
        {
            return JsonValue.Create(value);
        }

        // A plain empty value is YAML's null, as 'null' and '~' are (and as expect.json
        // already reads it). An empty STRING is written quoted: "" or ''.
        if (value.Length == 0)
            return null;

        // YAML 1.1 null tokens.
        if (value is "null" or "Null" or "NULL" or "~")
            return null;

        // YAML 1.1 boolean tokens.
        if (value is "true" or "True" or "TRUE")
            return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE")
            return JsonValue.Create(false);

        // Integer / decimal literals (invariant culture) become JSON numbers. A double that
        // is not finite has no JSON spelling: .NET parses NaN, Infinity and an overflowing
        // exponent such as 1e400 without complaint, and serialising the result throws, so
        // such text is sent as the string it is.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d))
        {
            return JsonValue.Create(d);
        }

        // Everything else (including {placeholder} / ${secret:...} tokens) is a string.
        return JsonValue.Create(value);
    }

    /// <summary>
    /// Maps an explicitly tagged scalar to the JSON value its tag names, or refuses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tag wins over the style: <c>!!int "5"</c> is the number 5 and <c>!!str 123</c>
    /// the string "123". Content is checked against YAML 1.2's core schema:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>!!str</c> and the non-specific <c>!</c>: the text, as a string (an empty
    ///   value is <c>""</c>, not null).</item>
    ///   <item><c>!!int</c>: <c>[-+]?[0-9]+</c> (decimal, so <c>010</c> is 10, as for an
    ///   untagged value), <c>0o[0-7]+</c> or <c>0x[0-9a-fA-F]+</c>. A decimal integer of any
    ///   length is sent exactly; octal and hexadecimal must fit in 64 bits.</item>
    ///   <item><c>!!float</c>: a decimal number with optional fraction and exponent. JSON
    ///   has no infinity or NaN, so <c>.inf</c>/<c>.nan</c> and a value that overflows a
    ///   double are refused.</item>
    ///   <item><c>!!bool</c>: <c>true</c>, <c>True</c>, <c>TRUE</c>, <c>false</c>,
    ///   <c>False</c>, <c>FALSE</c>. YAML 1.1's <c>yes</c>/<c>no</c>/<c>on</c>/<c>off</c> are
    ///   refused, matching how an untagged <c>yes</c> stays a string.</item>
    ///   <item><c>!!null</c>: empty, <c>~</c>, <c>null</c>, <c>Null</c>, <c>NULL</c>.</item>
    ///   <item><c>!!timestamp</c>: the text as written, as a string — what the body sent
    ///   before tags were read, since JSON has no timestamp type.</item>
    /// </list>
    /// <para>
    /// Any other tag (a local tag such as <c>!money</c>, <c>!!binary</c>, a global tag) is
    /// refused naming the tag, rather than silently ignored. That agrees with schema
    /// validation, which already refuses an unresolved tag before any step binds.
    /// </para>
    /// </remarks>
    private static JsonValue? TaggedScalarToJsonNode(
        YamlScalarNode scalar,
        string value,
        YamlMappingNode step)
    {
        if (scalar.Tag.IsNonSpecific)
            return JsonValue.Create(value);

        var tag = scalar.Tag.Value;
        if (!tag.StartsWith(YamlTagPrefix, StringComparison.Ordinal))
            throw new RefusalException(DescribeUnsupportedTag(step, scalar));

        switch (tag.Substring(YamlTagPrefix.Length))
        {
            case "str":
            case "timestamp":
                return JsonValue.Create(value);

            case "null":
                if (value is "" or "~" or "null" or "Null" or "NULL")
                    return null;
                throw new RefusalException(DescribeTagMismatch(
                    step, scalar, "the value is not one of the null forms (empty, ~, null, Null, NULL)"));

            case "bool":
                if (value is "true" or "True" or "TRUE")
                    return JsonValue.Create(true);
                if (value is "false" or "False" or "FALSE")
                    return JsonValue.Create(false);
                throw new RefusalException(DescribeTagMismatch(
                    step, scalar, "the value is not one of true, True, TRUE, false, False, FALSE"));

            case "int":
                return ParseTaggedInt(scalar, value, step);

            case "float":
                return ParseTaggedFloat(scalar, value, step);

            default:
                throw new RefusalException(DescribeUnsupportedTag(step, scalar));
        }
    }

    /// <summary>Converts the content of a <c>!!int</c> scalar, or refuses it.</summary>
    private static JsonValue ParseTaggedInt(
        YamlScalarNode scalar,
        string value,
        YamlMappingNode step)
    {
        // 0o / 0x: core-schema octal and hexadecimal, unsigned, at most 64 bits.
        if (value.Length > 2 && value[0] == '0' && (value[1] == 'o' || value[1] == 'x'))
        {
            var radix = value[1] == 'o' ? 8 : 16;
            ulong accumulated = 0;
            var digits = value.AsSpan(2);
            var valid = true;
            foreach (var ch in digits)
            {
                var digit = ch switch
                {
                    >= '0' and <= '9' => ch - '0',
                    >= 'a' and <= 'f' => ch - 'a' + 10,
                    >= 'A' and <= 'F' => ch - 'A' + 10,
                    _ => int.MaxValue,
                };
                if (digit >= radix)
                {
                    valid = false;
                    break;
                }

                if (accumulated > (ulong.MaxValue - (ulong)digit) / (ulong)radix)
                {
                    throw new RefusalException(DescribeTagMismatch(
                        step, scalar, "the value does not fit in 64 bits"));
                }

                accumulated = (accumulated * (ulong)radix) + (ulong)digit;
            }

            if (valid)
                return JsonValue.Create(accumulated);
        }
        else
        {
            // [-+]?[0-9]+, read as decimal whatever its leading zeros.
            var start = value.Length > 0 && (value[0] == '-' || value[0] == '+') ? 1 : 0;
            var allDigits = value.Length > start;
            for (var i = start; i < value.Length && allDigits; i++)
                allDigits = value[i] is >= '0' and <= '9';

            if (allDigits)
            {
                if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    return JsonValue.Create(l);

                // Beyond 64 bits: JSON numbers have no width, so send the exact integer as a
                // canonical JSON number token rather than rounding it through a double.
                var negative = value[0] == '-';
                var significant = value.AsSpan(start).TrimStart('0');
                var canonical = (negative ? "-" : string.Empty) + significant.ToString();
                return JsonNode.Parse(canonical)!.AsValue();
            }
        }

        throw new RefusalException(DescribeTagMismatch(
            step, scalar, "the value is not an integer (digits with an optional sign, or 0o / 0x followed by octal / hexadecimal digits)"));
    }

    /// <summary>Converts the content of a <c>!!float</c> scalar, or refuses it.</summary>
    private static JsonValue ParseTaggedFloat(
        YamlScalarNode scalar,
        string value,
        YamlMappingNode step)
    {
        if (value is ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF"
            or "-.inf" or "-.Inf" or "-.INF" or ".nan" or ".NaN" or ".NAN")
        {
            throw new RefusalException(DescribeTagMismatch(
                step, scalar, "JSON has no infinity or NaN"));
        }

        if (!IsCoreSchemaFloat(value))
        {
            throw new RefusalException(DescribeTagMismatch(
                step, scalar, "the value is not a number (digits with an optional sign, fraction and exponent)"));
        }

        var d = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(d))
        {
            throw new RefusalException(DescribeTagMismatch(
                step, scalar, "the value is too large for a JSON number"));
        }

        return JsonValue.Create(d);
    }

    /// <summary>
    /// True for YAML 1.2 core-schema float text without the infinity and NaN forms:
    /// <c>[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?</c>.
    /// </summary>
    private static bool IsCoreSchemaFloat(string value)
    {
        var i = 0;
        if (i < value.Length && (value[i] == '-' || value[i] == '+'))
            i++;

        var integerDigits = 0;
        while (i < value.Length && value[i] is >= '0' and <= '9')
        {
            i++;
            integerDigits++;
        }

        var fractionDigits = 0;
        if (i < value.Length && value[i] == '.')
        {
            i++;
            while (i < value.Length && value[i] is >= '0' and <= '9')
            {
                i++;
                fractionDigits++;
            }
        }

        if (integerDigits == 0 && fractionDigits == 0)
            return false;

        if (i < value.Length && (value[i] == 'e' || value[i] == 'E'))
        {
            i++;
            if (i < value.Length && (value[i] == '-' || value[i] == '+'))
                i++;

            var exponentDigits = 0;
            while (i < value.Length && value[i] is >= '0' and <= '9')
            {
                i++;
                exponentDigits++;
            }

            if (exponentDigits == 0)
                return false;
        }

        return i == value.Length;
    }
}
