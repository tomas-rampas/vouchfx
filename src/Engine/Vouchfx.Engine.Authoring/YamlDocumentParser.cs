// Vouchfx.Engine.Authoring — YamlDocumentParser (S03-B-01).
//
// Parses a .e2e.yaml string into a strongly-typed E2eDocument using YamlDotNet's
// representation model so that:
//   - YamlMappingNode references are available for RawNode / Seed / Extra.
//   - YamlNode.Start (Mark with Line/Column) is retained for B-02/B-03 diagnostics.
//
// Design notes:
//   - Uses YamlStream.Load (representation model), NOT the Deserializer<T> convention
//     path, to preserve node identity and line information throughout.
//   - Semantic validation (required `steps`, minItems, step-type constraints) is the
//     responsibility of the JSON Schema layer (a later task).  The parser is
//     deliberately lenient: an absent `steps` section produces an empty list rather
//     than an exception, allowing the schema validator to emit a user-facing message.
//   - All known YAML scalars are extracted defensively (missing key → null / default).
//     Unknown top-level keys are silently ignored to support forward compatibility.

using System.Globalization;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Authoring;

/// <summary>
/// Parses a <c>.e2e.yaml</c> string into a strongly-typed <see cref="E2eDocument"/>.
/// </summary>
/// <remarks>
/// Uses YamlDotNet's representation model so that raw <see cref="YamlMappingNode"/>
/// references and line/column positions (<see cref="YamlNode.Start"/>) are available
/// to downstream consumers (provider binders, the schema validator, and the LSP).
/// </remarks>
public static class YamlDocumentParser
{
    /// <summary>
    /// Deserialises <paramref name="yamlText"/> into an <see cref="E2eDocument"/>.
    /// </summary>
    /// <param name="yamlText">The full text of a <c>.e2e.yaml</c> file.</param>
    /// <returns>A populated <see cref="E2eDocument"/>.</returns>
    /// <exception cref="YamlParseException">
    /// Thrown when the input is empty, has a non-mapping root node, or contains
    /// malformed YAML that YamlDotNet cannot parse.
    /// </exception>
    public static E2eDocument Parse(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            throw new YamlParseException("The YAML input is empty or contains only whitespace.");
        }

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(yamlText));
        }
        catch (YamlException ex)
        {
            throw new YamlParseException(
                $"YAML parse error at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}",
                ex.Start.Line,
                ex.Start.Column,
                ex);
        }

        if (stream.Documents.Count == 0)
        {
            throw new YamlParseException("The YAML input contains no documents.");
        }

        var root = stream.Documents[0].RootNode;
        if (root is not YamlMappingNode rootMapping)
        {
            throw new YamlParseException(
                $"Expected the root YAML node to be a mapping, but found {root.NodeType} at line {root.Start.Line}, column {root.Start.Column}.",
                root.Start.Line,
                root.Start.Column);
        }

        var metadata = ParseMetadata(rootMapping);
        var environment = ParseEnvironment(rootMapping);
        var variables = ParseVariables(rootMapping);
        var steps = ParseSteps(rootMapping);

        return new E2eDocument(metadata, environment, variables, steps);
    }

    // -------------------------------------------------------------------------
    // Section parsers
    // -------------------------------------------------------------------------

    private static MetadataSpec? ParseMetadata(YamlMappingNode root)
    {
        if (!TryGetMapping(root, "metadata", out var node))
        {
            return null;
        }

        var name = GetScalar(node, "name");
        var owner = GetScalar(node, "owner");
        var description = GetScalar(node, "description");
        var schemaVersion = GetScalar(node, "schemaVersion");
        var tags = GetStringSequence(node, "tags");

        return new MetadataSpec(name, owner, tags, description, schemaVersion);
    }

    private static EnvironmentSpec? ParseEnvironment(YamlMappingNode root)
    {
        if (!TryGetMapping(root, "environment", out var node))
        {
            return null;
        }

        var imageRegistry = GetScalar(node, "imageRegistry");
        var imagePullPolicy = GetScalar(node, "imagePullPolicy");

        var services = ParseServiceMap(node);
        var dependencies = ParseDependencyMap(node);
        var seed = ParseSeed(node);

        return new EnvironmentSpec(services, dependencies, seed, imageRegistry, imagePullPolicy);
    }

    /// <summary>
    /// Parses the optional <c>environment.seed</c> block (docs/02 §3.2.2) into a
    /// strongly-typed <see cref="SeedSpec"/>.
    /// </summary>
    /// <remarks>
    /// Grammar:
    /// <code>
    /// seed:
    ///   orders-db:                       # postgres → SQL (A-01)
    ///     sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
    ///   events:                          # broker → warm-up publish (A-02)
    ///     publish:
    ///       - topic: catalog.snapshot
    ///         payload: { from: "fixtures/catalog.json" }
    ///   catalog-store:                   # document store → document fixture (A-02)
    ///     documents:
    ///       - collection: products
    ///         from: "fixtures/products.json"
    /// </code>
    /// Each top-level key is a logical dependency name; under it the dependency
    /// declares one seed kind (<c>sql</c>, <c>publish</c>, or <c>documents</c>).
    /// The parser binds whichever kinds are present; the seed applier later
    /// dispatches on the dependency's declared <c>type</c> and rejects a mismatch.
    /// Returns <see langword="null"/> only when the <c>seed</c> block is absent, is
    /// not a mapping, or declares no dependency keys at all; otherwise returns a
    /// <see cref="SeedSpec"/> containing every declared dependency. A dependency
    /// mapping that names none of the seed kinds is retained as a no-op (a
    /// <see cref="DependencySeed"/> with all kinds <see langword="null"/>), which the
    /// seed applier later skips.
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when a dependency's value is not a mapping (e.g. a bare scalar file
    /// path where a <c>{ sql: [...] }</c> mapping is expected), when its <c>sql</c>
    /// entry is present but is not a sequence of scalars, when a <c>publish</c> item
    /// is missing its <c>topic</c> / <c>payload.from</c>, or when a <c>documents</c>
    /// item is missing its <c>from</c>.  Rejecting a malformed dependency rather
    /// than dropping it prevents a later misattributed assertion <c>Fail</c> (§12.1).
    /// </exception>
    private static SeedSpec? ParseSeed(YamlMappingNode environment)
    {
        if (!TryGetMapping(environment, "seed", out var seedNode))
        {
            return null;
        }

        var dependencies = new Dictionary<string, DependencySeed>(StringComparer.Ordinal);
        foreach (var (key, value) in seedNode.Children)
        {
            if (key is not YamlScalarNode keyScalar || keyScalar.Value is null)
            {
                // Reject a malformed seed dependency key rather than silently dropping it: a
                // skipped dependency leaves a fixture unloaded, surfacing later as a
                // misattributed assertion Fail/EnvironmentError — the exact §12.1 confusion
                // seeding prevents.  Mirrors the value-side throw below and ParseCaptureMap.
                throw new YamlParseException(
                    $"seed dependency key at line {key.Start.Line} must be a scalar (the logical " +
                    $"dependency name), but found {key.NodeType}.",
                    key.Start.Line,
                    key.Start.Column);
            }

            if (value is not YamlMappingNode depMapping)
            {
                // A dependency value that is not a mapping (e.g. a bare scalar file
                // path) is a malformed shape.  Silently dropping it would later
                // surface as a misattributed assertion Fail — the exact §12.1
                // confusion seeding prevents — so reject it with line/column,
                // mirroring ParseSeedSqlSequence's rigour for a malformed 'sql'.
                throw new YamlParseException(
                    $"Seed dependency '{keyScalar.Value}' at line {value.Start.Line} must be a " +
                    $"mapping with a 'sql', 'publish', or 'documents' entry " +
                    $"(e.g. 'sql: [ \"fixtures/a.sql\" ]'), but found {value.NodeType}.",
                    value.Start.Line,
                    value.Start.Column);
            }

            var sql = ParseSeedSqlSequence(keyScalar.Value, depMapping);
            var publish = ParseSeedPublishSequence(keyScalar.Value, depMapping);
            var documents = ParseSeedDocumentsSequence(keyScalar.Value, depMapping);
            dependencies[keyScalar.Value] = new DependencySeed(sql, publish, documents);
        }

        return dependencies.Count > 0 ? new SeedSpec(dependencies) : null;
    }

    /// <summary>
    /// Reads the <c>sql</c> entry of a single seed dependency mapping as a
    /// sequence of scalar file paths.  Returns <see langword="null"/> when the
    /// <c>sql</c> key is absent.
    /// </summary>
    /// <exception cref="YamlParseException">
    /// Thrown when <c>sql</c> is present but is not a sequence, or when any of its
    /// items is not a scalar.
    /// </exception>
    private static List<string>? ParseSeedSqlSequence(
        string dependencyName,
        YamlMappingNode depMapping)
    {
        if (!TryGetNode(depMapping, "sql", out var sqlNode))
        {
            return null;
        }

        if (sqlNode is not YamlSequenceNode sequence)
        {
            throw new YamlParseException(
                $"Seed dependency '{dependencyName}' at line {sqlNode.Start.Line} has a " +
                $"'sql' entry that is not a sequence; expected a list of file paths.",
                sqlNode.Start.Line,
                sqlNode.Start.Column);
        }

        var list = new List<string>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode scalar || scalar.Value is null)
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {item.Start.Line} has a " +
                    $"'sql' item that is not a scalar file path.",
                    item.Start.Line,
                    item.Start.Column);
            }

            list.Add(scalar.Value);
        }

        return list;
    }

    /// <summary>
    /// Reads the <c>publish</c> entry of a single seed dependency mapping as a
    /// sequence of broker warm-up messages (docs/02 §3.2.2).  Returns
    /// <see langword="null"/> when the <c>publish</c> key is absent.
    /// </summary>
    /// <remarks>
    /// Grammar:
    /// <code>
    /// publish:
    ///   - topic: catalog.snapshot
    ///     payload: { from: "fixtures/catalog.json" }
    /// </code>
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when <c>publish</c> is present but is not a sequence, when an item is
    /// not a mapping, or when an item is missing the required <c>topic</c> scalar
    /// or <c>payload.from</c> scalar.  Rejecting a malformed entry rather than
    /// dropping it prevents a later misattributed assertion <c>Fail</c> (§12.1).
    /// </exception>
    private static List<PublishSeed>? ParseSeedPublishSequence(
        string dependencyName,
        YamlMappingNode depMapping)
    {
        if (!TryGetNode(depMapping, "publish", out var publishNode))
        {
            return null;
        }

        if (publishNode is not YamlSequenceNode sequence)
        {
            throw new YamlParseException(
                $"Seed dependency '{dependencyName}' at line {publishNode.Start.Line} has a " +
                $"'publish' entry that is not a sequence; expected a list of warm-up messages.",
                publishNode.Start.Line,
                publishNode.Start.Column);
        }

        var list = new List<PublishSeed>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode messageMapping)
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {item.Start.Line} has a " +
                    $"'publish' item that is not a mapping; expected " +
                    $"'{{ topic: ..., payload: {{ from: ... }} }}'.",
                    item.Start.Line,
                    item.Start.Column);
            }

            var topic = GetScalar(messageMapping, "topic");
            if (string.IsNullOrEmpty(topic))
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {messageMapping.Start.Line} has a " +
                    $"'publish' item missing the required 'topic' scalar.",
                    messageMapping.Start.Line,
                    messageMapping.Start.Column);
            }

            if (!TryGetMapping(messageMapping, "payload", out var payloadMapping))
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {messageMapping.Start.Line} has a " +
                    $"'publish' item missing the required 'payload' mapping " +
                    $"(expected 'payload: {{ from: ... }}').",
                    messageMapping.Start.Line,
                    messageMapping.Start.Column);
            }

            var payloadFrom = GetScalar(payloadMapping, "from");
            if (string.IsNullOrEmpty(payloadFrom))
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {payloadMapping.Start.Line} has a " +
                    $"'publish' item whose 'payload' is missing the required 'from' file path.",
                    payloadMapping.Start.Line,
                    payloadMapping.Start.Column);
            }

            list.Add(new PublishSeed(topic, payloadFrom));
        }

        return list;
    }

    /// <summary>
    /// Reads the <c>documents</c> entry of a single seed dependency mapping as a
    /// sequence of document fixtures (docs/02 §3.2.2).  Returns
    /// <see langword="null"/> when the <c>documents</c> key is absent.
    /// </summary>
    /// <remarks>
    /// Grammar:
    /// <code>
    /// documents:
    ///   - collection: products       # optional target name
    ///     from: "fixtures/products.json"
    /// </code>
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when <c>documents</c> is present but is not a sequence, when an item
    /// is not a mapping, or when an item is missing the required <c>from</c>
    /// scalar.  The <c>collection</c> scalar is optional.
    /// </exception>
    private static List<DocumentSeed>? ParseSeedDocumentsSequence(
        string dependencyName,
        YamlMappingNode depMapping)
    {
        if (!TryGetNode(depMapping, "documents", out var documentsNode))
        {
            return null;
        }

        if (documentsNode is not YamlSequenceNode sequence)
        {
            throw new YamlParseException(
                $"Seed dependency '{dependencyName}' at line {documentsNode.Start.Line} has a " +
                $"'documents' entry that is not a sequence; expected a list of document fixtures.",
                documentsNode.Start.Line,
                documentsNode.Start.Column);
        }

        var list = new List<DocumentSeed>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode documentMapping)
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {item.Start.Line} has a " +
                    $"'documents' item that is not a mapping; expected " +
                    $"'{{ from: ..., collection: ... }}'.",
                    item.Start.Line,
                    item.Start.Column);
            }

            var from = GetScalar(documentMapping, "from");
            if (string.IsNullOrEmpty(from))
            {
                throw new YamlParseException(
                    $"Seed dependency '{dependencyName}' at line {documentMapping.Start.Line} has a " +
                    $"'documents' item missing the required 'from' file path.",
                    documentMapping.Start.Line,
                    documentMapping.Start.Column);
            }

            // 'collection' is optional (a document store may have a single default container).
            var collection = GetScalar(documentMapping, "collection");

            list.Add(new DocumentSeed(collection, from));
        }

        return list;
    }

    private static Dictionary<string, string>? ParseVariables(YamlMappingNode root)
    {
        if (!TryGetMapping(root, "variables", out var node))
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in node.Children)
        {
            if (key is YamlScalarNode keyScalar && keyScalar.Value is not null)
            {
                // Stringify all scalar values; non-scalar values are skipped.
                if (value is YamlScalarNode valueScalar)
                {
                    dict[keyScalar.Value] = valueScalar.Value ?? string.Empty;
                }
            }
        }

        return dict.Count > 0 ? dict : null;
    }

    private static IReadOnlyList<StepSpec> ParseSteps(YamlMappingNode root)
    {
        if (!TryGetNode(root, "steps", out var stepsNode) || stepsNode is not YamlSequenceNode sequence)
        {
            return Array.Empty<StepSpec>();
        }

        var list = new List<StepSpec>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode stepMapping)
            {
                throw new YamlParseException(
                    $"Each step must be a YAML mapping node, but found {item.NodeType} at line {item.Start.Line}, column {item.Start.Column}.",
                    item.Start.Line,
                    item.Start.Column);
            }

            list.Add(ParseStep(stepMapping));
        }

        return list;
    }

    // -------------------------------------------------------------------------
    // Service and dependency map parsers
    // -------------------------------------------------------------------------

    private static Dictionary<string, ServiceSpec>? ParseServiceMap(YamlMappingNode environment)
    {
        if (!TryGetMapping(environment, "services", out var servicesNode))
        {
            return null;
        }

        var dict = new Dictionary<string, ServiceSpec>(StringComparer.Ordinal);
        foreach (var (key, value) in servicesNode.Children)
        {
            if (key is not YamlScalarNode keyScalar || keyScalar.Value is null)
            {
                continue;
            }

            if (value is not YamlMappingNode serviceMapping)
            {
                // A service value that is not a mapping (e.g. a bare image scalar
                // where a '{ image: … }' mapping is expected) is a malformed shape.
                // Silently dropping it would leave the system-under-test container
                // unstarted, surfacing later as a misattributed EnvironmentError —
                // the exact §12.1 confusion the parser elsewhere prevents — so reject
                // it with line/column, mirroring the seed-dependency-value throw.
                throw new YamlParseException(
                    $"Service '{keyScalar.Value}' at line {value.Start.Line} must be a " +
                    $"mapping declaring the container's image or project " +
                    $"(e.g. 'image: myorg/api:1.0'), but found {value.NodeType}.",
                    value.Start.Line,
                    value.Start.Column);
            }

            var image = GetScalar(serviceMapping, "image");
            var project = GetScalar(serviceMapping, "project");
            var pullPolicy = GetScalar(serviceMapping, "imagePullPolicy");
            var httpPortRaw = GetScalar(serviceMapping, "httpPort");
            int? httpPort = httpPortRaw is not null && int.TryParse(httpPortRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
            var env = ParseEnvMap(serviceMapping, keyScalar.Value);

            dict[keyScalar.Value] = new ServiceSpec(image, project, pullPolicy, httpPort, env);
        }

        return dict.Count > 0 ? dict : null;
    }

    /// <summary>
    /// Parses a service's optional <c>env:</c> mapping (SUT configuration surface) into a
    /// strongly-typed <c>string -&gt; string</c> dictionary.
    /// </summary>
    /// <remarks>
    /// Every value is retained in its RAW scalar form — a bare <c>8080</c> or <c>true</c>
    /// arrives from YamlDotNet as a scalar string ("8080"/"true"), which is exactly the
    /// literal text a container's environment variable needs; no numeric/boolean coercion is
    /// applied here (the YAML-scalar-coercion gotcha this parser is elsewhere careful about).
    /// Reference resolution (<c>${conn:name}</c> / <c>${conn:name.part}</c>) and
    /// <c>${secret:...}</c> rejection are the orchestration-layer mapper's job (§17) — this
    /// parser only extracts the literal text.
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when the <c>env:</c> node is present but is not a mapping, when a key is not a
    /// scalar, or when an entry's value is not a scalar (e.g. a nested mapping/sequence where
    /// a plain string is expected).
    /// </exception>
    private static Dictionary<string, string>? ParseEnvMap(YamlMappingNode serviceMapping, string serviceName)
    {
        if (!TryGetNode(serviceMapping, "env", out var envNode))
        {
            return null;
        }

        if (envNode is not YamlMappingNode envMapping)
        {
            throw new YamlParseException(
                $"Service '{serviceName}' 'env' at line {envNode.Start.Line} must be a mapping of " +
                $"environment-variable name to string value (e.g. 'FOO: \"bar\"'), but found {envNode.NodeType}.",
                envNode.Start.Line,
                envNode.Start.Column);
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in envMapping.Children)
        {
            if (key is not YamlScalarNode keyScalar || keyScalar.Value is null)
            {
                throw new YamlParseException(
                    $"Service '{serviceName}' 'env' key at line {key.Start.Line} must be a scalar " +
                    $"environment-variable name, but found {key.NodeType}.",
                    key.Start.Line,
                    key.Start.Column);
            }

            if (value is not YamlScalarNode valueScalar || valueScalar.Value is null)
            {
                throw new YamlParseException(
                    $"Service '{serviceName}' env entry '{keyScalar.Value}' at line {value.Start.Line} " +
                    $"must be a scalar string value, but found {value.NodeType}.",
                    value.Start.Line,
                    value.Start.Column);
            }

            dict[keyScalar.Value] = valueScalar.Value;
        }

        return dict.Count > 0 ? dict : null;
    }

    private static Dictionary<string, DependencySpec>? ParseDependencyMap(YamlMappingNode environment)
    {
        if (!TryGetMapping(environment, "dependencies", out var depsNode))
        {
            return null;
        }

        var dict = new Dictionary<string, DependencySpec>(StringComparer.Ordinal);
        foreach (var (key, value) in depsNode.Children)
        {
            if (key is not YamlScalarNode keyScalar || keyScalar.Value is null)
            {
                continue;
            }

            if (value is not YamlMappingNode depMapping)
            {
                // A dependency value that is not a mapping (e.g. a bare type scalar
                // where a '{ type: … }' mapping is expected) is a malformed shape.
                // Silently dropping it would leave a managed Aspire resource
                // unprovisioned, surfacing later as a misattributed EnvironmentError —
                // the exact §12.1 confusion the parser elsewhere prevents — so reject
                // it with line/column, mirroring the missing-'type' throw below.
                throw new YamlParseException(
                    $"Dependency '{keyScalar.Value}' at line {value.Start.Line} must be a " +
                    $"mapping declaring at least its 'type' " +
                    $"(e.g. 'type: postgres'), but found {value.NodeType}.",
                    value.Start.Line,
                    value.Start.Column);
            }

            var type = GetScalar(depMapping, "type");
            if (type is null)
            {
                throw new YamlParseException(
                    $"Dependency '{keyScalar.Value}' at line {depMapping.Start.Line} is missing the required 'type' field.",
                    depMapping.Start.Line,
                    depMapping.Start.Column);
            }

            var version = GetScalar(depMapping, "version");
            var image = GetScalar(depMapping, "image");

            // Collect any extra fields (everything except 'type', 'version', and
            // 'image') into a new mapping node so provider resource contributors
            // can bind them.
            YamlMappingNode? extra = BuildExtraNode(depMapping, "type", "version", "image");

            dict[keyScalar.Value] = new DependencySpec(type, version, extra) { Image = image };
        }

        return dict.Count > 0 ? dict : null;
    }

    // -------------------------------------------------------------------------
    // Step parser
    // -------------------------------------------------------------------------

    private static StepSpec ParseStep(YamlMappingNode stepMapping)
    {
        var id = GetScalar(stepMapping, "id");
        var type = GetScalar(stepMapping, "type");

        if (id is null)
        {
            throw new YamlParseException(
                $"A step at line {stepMapping.Start.Line} is missing the required 'id' field.",
                stepMapping.Start.Line,
                stepMapping.Start.Column);
        }

        if (type is null)
        {
            throw new YamlParseException(
                $"Step '{id}' at line {stepMapping.Start.Line} is missing the required 'type' field.",
                stepMapping.Start.Line,
                stepMapping.Start.Column);
        }

        var description = GetScalar(stepMapping, "description");
        var verifyMode = GetScalar(stepMapping, "verifyMode");
        var timeout = GetScalar(stepMapping, "timeout");
        var continueOnFailureRaw = GetScalar(stepMapping, "continueOnFailure");
        bool? continueOnFailure = continueOnFailureRaw is not null
            ? string.Equals(continueOnFailureRaw, "true", StringComparison.OrdinalIgnoreCase)
            : null;

        var capture = ParseCaptureMap(stepMapping);

        return new StepSpec(id, type, description, capture, verifyMode, timeout, continueOnFailure, stepMapping);
    }

    /// <summary>
    /// Parses the optional <c>capture:</c> block of a step (DSL §6.1) into a map
    /// of variable name to a typed <see cref="CaptureExpr"/> (S07-B-01a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two authoring forms are accepted per entry and are 100% interchangeable
    /// with respect to the bare-scalar form's prior behaviour:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     A <strong>bare scalar</strong> (<c>name: "$.id"</c>) is bound to a
    ///     <see cref="CaptureExpr"/> with <see cref="CaptureFormat.JsonPath"/>,
    ///     preserving the exact pre-S07 semantics (every existing scalar capture
    ///     parses and behaves identically).
    ///   </description></item>
    ///   <item><description>
    ///     A <strong>single-key mapping</strong> selects the format explicitly:
    ///     <c>name: { jsonpath: "$.id" }</c> →
    ///     <see cref="CaptureFormat.JsonPath"/>, or
    ///     <c>name: { xpath: "//id" }</c> → <see cref="CaptureFormat.XPath"/>.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The format is never inferred from the shape of the expression string; an
    /// author who needs XPath must say so via the mapping form.
    /// </para>
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown, with 1-based line/column context mirroring the seed parsers, when a
    /// capture value is neither a scalar nor a mapping, when the mapping declares
    /// neither <c>jsonpath</c> nor <c>xpath</c>, declares both, declares an
    /// unknown key, or carries a non-scalar expression value.  Rejecting a
    /// malformed capture rather than silently dropping it prevents a later
    /// misattributed assertion <c>Fail</c> (§12.1).
    /// </exception>
    private static Dictionary<string, CaptureExpr>? ParseCaptureMap(YamlMappingNode stepMapping)
    {
        if (!TryGetMapping(stepMapping, "capture", out var captureNode))
        {
            return null;
        }

        var dict = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
        foreach (var (key, value) in captureNode.Children)
        {
            if (key is not YamlScalarNode keyScalar || keyScalar.Value is null)
            {
                // Reject a malformed capture key rather than silently dropping the
                // entry: a skipped capture leaves a {placeholder} unresolved or sends
                // the step Inconclusive later, misattributing the cause (§12.1).  The
                // 1-based line/column are derived from the offending key node, mirroring
                // the seed parsers and the ParseCaptureEntry diagnostics.
                throw new YamlParseException(
                    $"capture key at line {key.Start.Line} must be a scalar (the variable " +
                    $"name to bind), but found {key.NodeType}.",
                    key.Start.Line,
                    key.Start.Column);
            }

            dict[keyScalar.Value] = ParseCaptureEntry(keyScalar.Value, value);
        }

        return dict.Count > 0 ? dict : null;
    }

    // The two recognised keys of the explicit single-key capture mapping form.
    private const string CaptureKeyJsonPath = "jsonpath";
    private const string CaptureKeyXPath = "xpath";

    /// <summary>
    /// Binds a single <c>capture:</c> entry value (either a bare scalar or an
    /// explicit single-key mapping) to a <see cref="CaptureExpr"/>.
    /// </summary>
    /// <param name="captureName">
    /// The author-supplied variable name (the mapping key), used only for
    /// diagnostics.
    /// </param>
    /// <param name="value">The YAML node holding the entry's value.</param>
    /// <returns>The typed extractor for this entry.</returns>
    /// <exception cref="YamlParseException">See <see cref="ParseCaptureMap"/>.</exception>
    private static CaptureExpr ParseCaptureEntry(string captureName, YamlNode value)
    {
        // ── Bare-scalar form (back-compat): defaults to JSONPath ──────────────
        if (value is YamlScalarNode scalar && scalar.Value is not null)
        {
            return new CaptureExpr(CaptureFormat.JsonPath, scalar.Value);
        }

        // ── Explicit single-key mapping form: { jsonpath: … } | { xpath: … } ──
        if (value is not YamlMappingNode mapping)
        {
            throw new YamlParseException(
                $"capture entry '{captureName}' at line {value.Start.Line} must be either a " +
                $"scalar expression (e.g. '\"$.id\"', which defaults to JSONPath) or a single-key " +
                $"mapping selecting the format (e.g. '{{ xpath: \"//id\" }}' or " +
                $"'{{ jsonpath: \"$.id\" }}'), but found {value.NodeType}.",
                value.Start.Line,
                value.Start.Column);
        }

        string? jsonPath = null;
        string? xPath = null;

        foreach (var (mapKey, mapValue) in mapping.Children)
        {
            if (mapKey is not YamlScalarNode mapKeyScalar || mapKeyScalar.Value is null)
            {
                continue;
            }

            switch (mapKeyScalar.Value)
            {
                case CaptureKeyJsonPath:
                    jsonPath = RequireCaptureExpressionScalar(captureName, CaptureKeyJsonPath, mapValue);
                    break;

                case CaptureKeyXPath:
                    xPath = RequireCaptureExpressionScalar(captureName, CaptureKeyXPath, mapValue);
                    break;

                default:
                    throw new YamlParseException(
                        $"capture entry '{captureName}' at line {mapKeyScalar.Start.Line} has an " +
                        $"unknown key '{mapKeyScalar.Value}'; the only recognised keys are " +
                        $"'{CaptureKeyJsonPath}' and '{CaptureKeyXPath}'.",
                        mapKeyScalar.Start.Line,
                        mapKeyScalar.Start.Column);
            }
        }

        if (jsonPath is null && xPath is null)
        {
            throw new YamlParseException(
                $"capture entry '{captureName}' at line {mapping.Start.Line} is an empty mapping; it " +
                $"must declare exactly one of '{CaptureKeyJsonPath}' or '{CaptureKeyXPath}' " +
                $"(e.g. '{{ jsonpath: \"$.id\" }}' or '{{ xpath: \"//id\" }}').",
                mapping.Start.Line,
                mapping.Start.Column);
        }

        if (jsonPath is not null && xPath is not null)
        {
            throw new YamlParseException(
                $"capture entry '{captureName}' at line {mapping.Start.Line} declares both " +
                $"'{CaptureKeyJsonPath}' and '{CaptureKeyXPath}'; declare exactly one so the " +
                $"extractor format is unambiguous.",
                mapping.Start.Line,
                mapping.Start.Column);
        }

        return jsonPath is not null
            ? new CaptureExpr(CaptureFormat.JsonPath, jsonPath)
            : new CaptureExpr(CaptureFormat.XPath, xPath!);
    }

    /// <summary>
    /// Reads the scalar expression value of a capture mapping key
    /// (<c>jsonpath</c> / <c>xpath</c>), rejecting a non-scalar value.
    /// </summary>
    /// <exception cref="YamlParseException">
    /// Thrown when <paramref name="value"/> is not a non-null scalar.
    /// </exception>
    private static string RequireCaptureExpressionScalar(
        string captureName,
        string formatKey,
        YamlNode value)
    {
        if (value is YamlScalarNode scalar && scalar.Value is not null)
        {
            return scalar.Value;
        }

        throw new YamlParseException(
            $"capture entry '{captureName}' at line {value.Start.Line} has a '{formatKey}' value " +
            $"that is not a scalar expression string.",
            value.Start.Line,
            value.Start.Column);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a new <see cref="YamlMappingNode"/> from all children of
    /// <paramref name="source"/> whose key is not in <paramref name="excludedKeys"/>.
    /// Returns <see langword="null"/> when no remaining children exist.
    /// </summary>
    private static YamlMappingNode? BuildExtraNode(YamlMappingNode source, params string[] excludedKeys)
    {
        var excluded = new HashSet<string>(excludedKeys, StringComparer.Ordinal);
        var extra = new YamlMappingNode();
        foreach (var (key, value) in source.Children)
        {
            if (key is YamlScalarNode keyScalar && keyScalar.Value is not null
                && !excluded.Contains(keyScalar.Value))
            {
                extra.Children.Add(key, value);
            }
        }

        return extra.Children.Count > 0 ? extra : null;
    }

    /// <summary>
    /// Tries to retrieve the child node for <paramref name="key"/> from
    /// <paramref name="mapping"/>.
    /// </summary>
    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode node)
    {
        var scalarKey = new YamlScalarNode(key);
        if (mapping.Children.TryGetValue(scalarKey, out node!))
        {
            return true;
        }

        node = null!;
        return false;
    }

    /// <summary>
    /// Tries to retrieve a child <see cref="YamlMappingNode"/> for
    /// <paramref name="key"/> from <paramref name="mapping"/>.
    /// Returns <see langword="false"/> when the key is absent or the value is
    /// not a mapping.
    /// </summary>
    private static bool TryGetMapping(YamlMappingNode mapping, string key, out YamlMappingNode child)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlMappingNode m)
        {
            child = m;
            return true;
        }

        child = null!;
        return false;
    }

    /// <summary>
    /// Returns the string value of a scalar child node, or <see langword="null"/>
    /// when the key is absent or the value is not a scalar.
    /// </summary>
    private static string? GetScalar(YamlMappingNode mapping, string key)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlScalarNode scalar)
        {
            return scalar.Value;
        }

        return null;
    }

    /// <summary>
    /// Returns the elements of a sequence of scalar strings, or
    /// <see langword="null"/> when the key is absent or the value is not a sequence.
    /// Non-scalar items in the sequence are skipped.
    /// </summary>
    private static List<string>? GetStringSequence(YamlMappingNode mapping, string key)
    {
        if (!TryGetNode(mapping, key, out var node) || node is not YamlSequenceNode sequence)
        {
            return null;
        }

        var list = new List<string>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is YamlScalarNode scalar && scalar.Value is not null)
            {
                list.Add(scalar.Value);
            }
        }

        return list.Count > 0 ? list : null;
    }
}
