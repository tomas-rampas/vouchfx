// Platform.Engine.Authoring — YamlDocumentParser (S03-B-01).
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
using Platform.Engine.Authoring.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Authoring;

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
    ///   orders-db:
    ///     sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
    /// </code>
    /// Each top-level key is a logical dependency name; under it, <c>sql</c> is a
    /// sequence of scalar file paths.  Returns <see langword="null"/> when the
    /// <c>seed</c> block is absent, is not a mapping, or contains no usable
    /// dependency entries.
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when a dependency's value is not a mapping (e.g. a bare scalar file
    /// path where a <c>{ sql: [...] }</c> mapping is expected), or when its
    /// <c>sql</c> entry is present but is not a sequence of scalars.  Rejecting a
    /// malformed dependency rather than dropping it prevents a later misattributed
    /// assertion <c>Fail</c> (§12.1).
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
                continue;
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
                    $"mapping with a 'sql' sequence (e.g. 'sql: [ \"fixtures/a.sql\" ]'), " +
                    $"but found {value.NodeType}.",
                    value.Start.Line,
                    value.Start.Column);
            }

            var sql = ParseSeedSqlSequence(keyScalar.Value, depMapping);
            dependencies[keyScalar.Value] = new DependencySeed(sql);
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
                continue;
            }

            var image = GetScalar(serviceMapping, "image");
            var project = GetScalar(serviceMapping, "project");
            var pullPolicy = GetScalar(serviceMapping, "imagePullPolicy");
            var httpPortRaw = GetScalar(serviceMapping, "httpPort");
            int? httpPort = httpPortRaw is not null && int.TryParse(httpPortRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;

            dict[keyScalar.Value] = new ServiceSpec(image, project, pullPolicy, httpPort);
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
                continue;
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

            // Collect any extra fields (everything except 'type' and 'version') into
            // a new mapping node so provider resource contributors can bind them.
            YamlMappingNode? extra = BuildExtraNode(depMapping, "type", "version");

            dict[keyScalar.Value] = new DependencySpec(type, version, extra);
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

    private static Dictionary<string, string>? ParseCaptureMap(YamlMappingNode stepMapping)
    {
        if (!TryGetMapping(stepMapping, "capture", out var captureNode))
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in captureNode.Children)
        {
            if (key is YamlScalarNode keyScalar && keyScalar.Value is not null
                && value is YamlScalarNode valueScalar && valueScalar.Value is not null)
            {
                dict[keyScalar.Value] = valueScalar.Value;
            }
        }

        return dict.Count > 0 ? dict : null;
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
