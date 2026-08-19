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
    /// Parses the optional <c>environment.seed</c> block (docs/02 §3.2.5) into a
    /// strongly-typed <see cref="SeedSpec"/>.
    /// </summary>
    /// <remarks>
    /// Grammar:
    /// <code>
    /// seed:
    ///   orders-db:                       # postgres/sqlserver/mysql → SQL
    ///     sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
    /// </code>
    /// Each top-level key is a logical dependency name; <c>sql</c> is the only
    /// seed kind the v1 language recognises (the <c>publish</c>/<c>documents</c>
    /// wired-but-deferred seams were removed — see this file's header remarks and
    /// <c>SeedSpec.cs</c>'s). The parser binds <c>sql</c> when present; the seed
    /// applier later dispatches on the dependency's declared <c>type</c> and
    /// rejects a mismatch. Returns <see langword="null"/> only when the
    /// <c>seed</c> block is absent, is not a mapping, or declares no dependency
    /// keys at all; otherwise returns a <see cref="SeedSpec"/> containing every
    /// declared dependency. A dependency mapping with no <c>sql</c> entry is
    /// retained as a no-op (a <see cref="DependencySeed"/> with <c>Sql</c> null),
    /// which the seed applier later skips.
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when a dependency's value is not a mapping (e.g. a bare scalar file
    /// path where a <c>{ sql: [...] }</c> mapping is expected), or when its
    /// <c>sql</c> entry is present but is not a sequence of scalars. Rejecting a
    /// malformed dependency rather than dropping it prevents a later
    /// misattributed assertion <c>Fail</c> (§12.1).
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
                    $"mapping with a 'sql' entry " +
                    $"(e.g. 'sql: [ \"fixtures/a.sql\" ]'), but found {value.NodeType}.",
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
            // Service only: an empty 'env: {}' collapses to null so the spelling stays
            // indistinguishable from absent — the reader retains it (see its <returns>) so a
            // dependency can tell the two apart (EDGE-002). The collapse lives at this call
            // site, not in the reader, because changing service 'env:' behaviour in any way is
            // out of scope for dependency-env. Pinned by
            // Parse_Env_EmptyMap_CollapsesToNullOnAServiceButIsRetainedOnADependency.
            var env = CollapseEmptyEnvToNull(ParseEnvMap(serviceMapping, "Service", keyScalar.Value));
            var security = ParseSecurity(serviceMapping, "Service", keyScalar.Value);
            var (ports, pinnedHostPorts) = ParseServicePorts(serviceMapping, keyScalar.Value);
            var healthCheck = ParseHealthCheck(serviceMapping);

            dict[keyScalar.Value] = new ServiceSpec(image, project, pullPolicy, httpPort, env)
            {
                Security = security,
                Ports = ports,
                PinnedHostPorts = pinnedHostPorts,
                HealthCheck = healthCheck,
            };
        }

        // REQ-025 / EDGE-012, ACROSS SERVICES. Two services pinning one host port is a fault in
        // the DOCUMENT — it can never run, on any host, at any time — so it belongs here, where
        // every service is in view at once, and not at topology-build time.
        //
        // MEASURED, and the measurement is why this moved: caught during the topology build it was
        // classified an ENVIRONMENT error, so `vouchfx validate` reported PASS and a flagless
        // `vouchfx run` exited 0 on a suite that cannot start. An authoring mistake was being
        // reported as an infrastructure fault, which is the one direction the taxonomy must not
        // bend. Caught here it is a parse error: `validate` exits 4 and so does `run` — measured
        // against the same document, both before and after.
        //
        // The topology-build check remains as a backstop rather than as duplication: `ports:` is
        // not the only way a spec acquires pins, since `ServiceSpec.PinnedHostPorts` is a public
        // init-only property that a programmatic caller can populate without passing through this
        // parser at all.
        var pinnedBy = new Dictionary<int, (string Service, int ContainerPort)>();
        foreach (var (serviceName, spec) in dict)
        {
            if (spec.PinnedHostPorts is not { Count: > 0 } pins)
            {
                continue;
            }

            foreach (var (containerPort, hostPort) in pins)
            {
                if (pinnedBy.TryGetValue(hostPort, out var owner))
                {
                    throw new YamlParseException(
                        $"Host port {hostPort} is pinned by two services: '{owner.Service}' for "
                        + $"container port {owner.ContainerPort}, and '{serviceName}' for container "
                        + $"port {containerPort}. One host port publishes one container port — give "
                        + "them different host ports.",
                        servicesNode.Start.Line,
                        servicesNode.Start.Column);
                }

                pinnedBy[hostPort] = (serviceName, containerPort);
            }
        }

        return dict.Count > 0 ? dict : null;
    }

    // -------------------------------------------------------------------------
    // Service ports / healthCheck parsers (services-generalisation, PR B) —
    // REQ-008 / REQ-009. Deliberately lenient, like ParseSecurity: requiredness and
    // per-type field shape are the JSON Schema layer's responsibility
    // ($defs/serviceHealthCheck); cross-referencing healthCheck.port against the
    // service's own declared ports/httpPort is EnvironmentMapper's job.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a service's optional <c>ports:</c> sequence (REQ-008) into a list of TCP
    /// port numbers. Each item is read as a bare integer scalar — unlike <c>httpPort</c>,
    /// this is a NEW field with no pre-existing engine behaviour to preserve, so it does
    /// not carry <c>httpPort</c>'s quoted-string/leading-zero-octal compatibility shape;
    /// an author writes a plain decimal integer per item.
    /// </summary>
    /// <exception cref="YamlParseException">
    /// Thrown when <c>ports</c> is present but is not a sequence, or when an item is not
    /// an integer scalar — mirroring <see cref="ParseServerArtifacts"/>'s rigour for a
    /// malformed list: silently dropping a malformed entry would leave a declared port
    /// unexposed, surfacing later as a misattributed connection failure instead of an
    /// authoring-time diagnostic.
    /// </exception>
    private static (List<int>? Ports, Dictionary<int, int>? Pinned) ParseServicePorts(
        YamlMappingNode serviceMapping, string serviceName)
    {
        if (!TryGetNode(serviceMapping, "ports", out var portsNode))
        {
            return (null, null);
        }

        if (portsNode is not YamlSequenceNode sequence)
        {
            throw new YamlParseException(
                $"Service '{serviceName}' 'ports' at line {portsNode.Start.Line} must be a sequence " +
                $"of TCP port numbers (e.g. '[9093, 9094]'), but found {portsNode.NodeType}.",
                portsNode.Start.Line,
                portsNode.Start.Column);
        }

        var list = new List<int>(sequence.Children.Count);
        var pinned = new Dictionary<int, int>();
        var hostPorts = new Dictionary<int, int>();

        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode { Value: { } rawValue })
            {
                throw new YamlParseException(
                    $"Service '{serviceName}' 'ports' item at line {item.Start.Line} must be a bare " +
                    $"integer TCP port number or a '<host>:<container>' string, but found "
                    + $"{item.NodeType}.",
                    item.Start.Line,
                    item.Start.Column);
            }

            int container;
            var colon = rawValue.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0)
            {
                // The bare-integer form: container port declared, host port allocated by the
                // orchestrator. It gets its OWN diagnostics rather than falling through to the
                // pair branch — a value that is plainly a single number and merely out of range
                // must not be told it should have been a pair.
                container = ParsePortHalf(rawValue, rawValue, "port", serviceName, item);
            }
            else
            {
                // The mapping form, '<host>:<container>' — docker-compose's ordering, which is
                // what the target deployment already writes.
                //
                // The one-colon guard is kept but is NOT what rejects '1:2:3' — measured, a
                // last-colon split rejects it anyway, because a half containing a colon fails the
                // digit test. It is kept because an input with two colons has no single reading
                // this parser should pick, and saying so beats reinterpreting it.
                if (rawValue.IndexOf(':', colon + 1) >= 0)
                {
                    throw Malformed(
                        serviceName,
                        item,
                        rawValue,
                        "it contains more than one ':'. The pinned form is exactly "
                        + "'<host>:<container>', e.g. '19093:9093'.");
                }

                var host = ParsePortHalf(rawValue, rawValue[..colon], "host port", serviceName, item);
                container = ParsePortHalf(
                    rawValue, rawValue[(colon + 1)..], "container port", serviceName, item);

                // Host ports below 1024 are refused outright. They are privileged on Linux (so a
                // CI runner running as root would bind them where a developer's machine would
                // not — a difference nobody wants to debug) and they are the well-known ports: a
                // suite pinning 443 or 5432 squats a real service's port on every interface for
                // the length of a run. The CONTAINER half is deliberately unconstrained — it
                // lives in the container's own namespace, where 80 is its most ordinary value.
                if (host < 1024)
                {
                    throw Malformed(
                        serviceName,
                        item,
                        rawValue,
                        $"its host port {host} is below 1024. Host ports 1..1023 are privileged "
                        + "and well-known — pinning one squats a real service's port on this "
                        + "machine for the whole run. Pin a host port in 1024..65535. (The "
                        + "container port has no such limit.)");
                }

                if (!pinned.TryAdd(container, host))
                {
                    throw Malformed(
                        serviceName,
                        item,
                        rawValue,
                        $"container port {container} is already pinned to host port "
                        + $"{pinned[container]}. One container port publishes on one host port.");
                }

                if (!hostPorts.TryAdd(host, container))
                {
                    throw Malformed(
                        serviceName,
                        item,
                        rawValue,
                        $"host port {host} is already pinned to container port {hostPorts[host]}. "
                        + "One host port publishes one container port.");
                }
            }

            // ACROSS BOTH FORMS. `ports: [9093, "19093:9093"]` declares one container port twice,
            // and neither the schema's `uniqueItems` (which compares JSON values, and an integer
            // is never equal to a string) nor a pinned-only duplicate check can see it. Left
            // alone it produces two endpoint declarations with one name and the orchestrator
            // refuses the topology with "an endpoint named 'tcp-9093' already exists" — an
            // internal-sounding failure for a plain authoring mistake.
            if (list.Contains(container))
            {
                throw Malformed(
                    serviceName,
                    item,
                    rawValue,
                    $"container port {container} is declared more than once in 'ports'. Declare it "
                    + "once, in either the bare or the '<host>:<container>' form.");
            }

            list.Add(container);
        }

        return (
            list.Count > 0 ? list : null,
            pinned.Count > 0 ? pinned : null);
    }

    /// <summary>Builds the author-facing rejection for one malformed <c>ports:</c> entry.</summary>
    private static YamlParseException Malformed(
        string serviceName, YamlNode item, string rawValue, string because) =>
        new($"Service '{serviceName}' 'ports' item '{rawValue}' at line {item.Start.Line} is "
            + $"invalid: {because}",
            item.Start.Line,
            item.Start.Column);

    /// <summary>
    /// Parses one port value — a whole bare entry, or one half of a pinned pair — as a bare
    /// decimal integer in 1..65535 with no leading zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Leading zeros are refused, and that is what stops one document meaning two
    /// things.</strong> This value IS re-read as YAML — an earlier note here claimed otherwise and
    /// was wrong: the schema bridge re-parses the document with a scalar resolver applying YAML
    /// 1.1, under which <c>0123</c> is OCTAL. Measured: <c>ports: [0123]</c> was seen as <b>83</b>
    /// by the schema and <b>123</b> by this parser, and <c>[00080]</c> threw inside the bridge.
    /// Refusing the spelling makes that divergence unreachable rather than merely documented, and
    /// it is already the published rule for the sibling <c>security.endpoint</c> ("decimal,
    /// 1–65535, no leading zero").
    /// </para>
    /// <para>
    /// <see cref="NumberStyles.None"/> refuses a leading sign, surrounding space and thousands
    /// separators, so <c>-1</c>, <c>+80</c> and <c> 80</c> never reach the range test as something
    /// that already parsed.
    /// </para>
    /// </remarks>
    private static int ParsePortHalf(
        string rawValue, string text, string role, string serviceName, YamlNode item)
    {
        if (text.Length == 0)
        {
            throw Malformed(serviceName, item, rawValue, $"its {role} is empty.");
        }

        if (text[0] == '0' && text.Length > 1)
        {
            throw Malformed(
                serviceName,
                item,
                rawValue,
                $"its {role} '{text}' has a leading zero. Write a plain decimal integer: a leading "
                + "zero is read as octal by one of the two YAML passes this document makes and as "
                + "decimal by the other, so the same text would name two different ports.");
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw Malformed(
                serviceName, item, rawValue, $"its {role} '{text}' is not a bare decimal integer.");
        }

        if (port is < 1 or > 65535)
        {
            throw Malformed(
                serviceName, item, rawValue, $"its {role} {port} is outside the range 1..65535.");
        }

        return port;
    }

    /// <summary>
    /// Parses a service's optional <c>healthCheck:</c> block (REQ-009) into a strongly-typed
    /// <see cref="HealthCheckSpec"/>. Takes no owner-name parameter (unlike
    /// <see cref="ParseSecurity"/>/<see cref="ParseServerArtifacts"/>): every field here is
    /// read leniently with no throw path of its own, so there is no diagnostic message to
    /// splice a service name into.
    /// </summary>
    private static HealthCheckSpec? ParseHealthCheck(YamlMappingNode serviceMapping)
    {
        if (!TryGetMapping(serviceMapping, "healthCheck", out var healthCheckNode))
        {
            return null;
        }

        var type = GetScalar(healthCheckNode, "type");
        var path = GetScalar(healthCheckNode, "path");
        var portRaw = GetScalar(healthCheckNode, "port");
        int? port = portRaw is not null
            && int.TryParse(portRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var p)
                ? p
                : null;

        return new HealthCheckSpec(type, path, port);
    }

    /// <summary>
    /// Parses a service's or dependency's optional <c>env:</c> mapping (the container
    /// configuration surface) into a strongly-typed <c>string -&gt; string</c> dictionary.
    /// </summary>
    /// <param name="ownerMapping">The service's or dependency's own YAML mapping node.</param>
    /// <param name="ownerLabel">
    /// <c>"Service"</c> or <c>"Dependency"</c> — the subject noun spliced into this method's
    /// diagnostics, exactly as <see cref="ParseSecurity"/> does with its own label. WIDENED
    /// rather than forked (dependency-env spec, REQ-001): this is generic
    /// mapping-to-dictionary logic that had only the noun <c>"Service"</c> baked into it, and
    /// two copies of it would be two places for the value-shape rules to drift apart. The
    /// service diagnostics are byte-identical to their pre-widening form for
    /// <c>ownerLabel: "Service"</c>, which is the constraint that widening had to meet.
    /// </param>
    /// <param name="ownerName">The service's or dependency's logical (map-key) name.</param>
    /// <returns>
    /// <see langword="null"/> when the owner declares no <c>env:</c> key at all; otherwise the
    /// declared mapping — INCLUDING an empty dictionary for an empty <c>env: {}</c>, so
    /// "declared, empty" stays distinguishable from "not declared" (dependency-env EDGE-002).
    /// <para>
    /// THIS READER'S CONTRACT IS NOT WHAT EVERY CALLER SHIPS: <see cref="ParseServiceMap"/>
    /// deliberately re-collapses an empty dictionary back to <see langword="null"/> for a
    /// SERVICE (via <see cref="CollapseEmptyEnvToNull"/>), because changing service
    /// <c>env:</c> behaviour in any way is out of scope for dependency-env — the change that
    /// widened this reader. That collapse is the caller's rule, not
    /// this reader's; both halves are pinned by
    /// <c>Parse_Env_EmptyMap_CollapsesToNullOnAServiceButIsRetainedOnADependency</c>, so
    /// neither this <c>&lt;returns&gt;</c> nor that call site can be "tidied" into agreement
    /// with the other without a test going red.
    /// </para>
    /// </returns>
    /// <remarks>
    /// Every value is retained in its RAW scalar form — a bare <c>8080</c> or <c>true</c>
    /// arrives from YamlDotNet as a scalar string ("8080"/"true"), which is exactly the
    /// literal text a container's environment variable needs; no numeric/boolean coercion is
    /// applied here (the YAML-scalar-coercion gotcha this parser is elsewhere careful about).
    /// This includes YAML's explicit null: MEASURED against the pinned YamlDotNet 16.3.0
    /// representation model, <c>FOO: ~</c> reads back as the literal one-character text
    /// <c>~</c> for a service and a dependency alike; this reader refuses neither. For a
    /// SERVICE it is the JSON Schema layer — whose <c>env</c> value type is
    /// <c>string | integer | number | boolean</c> — that refuses that spelling. For a
    /// DEPENDENCY there is no <c>env</c> schema yet, so the schema refuses the whole <c>env</c>
    /// KEY rather than that spelling of a value, and <c>$defs/dependency</c> gains the same
    /// value-type shape in the schema change that follows this one (dependency-env REQ-002).
    /// Reference resolution (<c>${conn:name}</c> / <c>${conn:name.part}</c> /
    /// <c>${env:NAME}</c>), the rejection of <c>${secret:...}</c> (§17), and the
    /// dependency-only refusals (a variable name the engine itself sets for that dependency's
    /// type; <c>${conn:}</c> on a dependency at all) are the orchestration-layer mapper's job
    /// — this parser only extracts the literal text.
    /// </remarks>
    /// <exception cref="YamlParseException">
    /// Thrown when the <c>env:</c> node is present but is not a mapping, when a key is not a
    /// scalar, or when an entry's value is not a scalar (e.g. a nested mapping/sequence where
    /// a plain string is expected).
    /// </exception>
    private static Dictionary<string, string>? ParseEnvMap(
        YamlMappingNode ownerMapping, string ownerLabel, string ownerName)
    {
        if (!TryGetNode(ownerMapping, "env", out var envNode))
        {
            return null;
        }

        if (envNode is not YamlMappingNode envMapping)
        {
            throw new YamlParseException(
                $"{ownerLabel} '{ownerName}' 'env' at line {envNode.Start.Line} must be a mapping of " +
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
                    $"{ownerLabel} '{ownerName}' 'env' key at line {key.Start.Line} must be a scalar " +
                    $"environment-variable name, but found {key.NodeType}.",
                    key.Start.Line,
                    key.Start.Column);
            }

            if (value is not YamlScalarNode valueScalar || valueScalar.Value is null)
            {
                throw new YamlParseException(
                    $"{ownerLabel} '{ownerName}' env entry '{keyScalar.Value}' at line {value.Start.Line} " +
                    $"must be a scalar string value, but found {value.NodeType}.",
                    value.Start.Line,
                    value.Start.Column);
            }

            dict[keyScalar.Value] = valueScalar.Value;
        }

        return dict;
    }

    /// <summary>
    /// Collapses an empty <c>env: {}</c> back to <see langword="null"/> so that, on a SERVICE,
    /// the empty spelling stays indistinguishable from a service that declared no <c>env:</c>
    /// at all.
    /// </summary>
    /// <remarks>
    /// The SERVICE call site's rule, deliberately NOT <see cref="ParseEnvMap"/>'s — that reader
    /// retains an empty declaration so a DEPENDENCY can distinguish "declared, empty" from "not
    /// declared" (dependency-env EDGE-002), and changing service <c>env:</c> behaviour in any
    /// way is out of scope for that change. Named rather than inlined so the rule describes
    /// itself and cannot drift from a comment; both halves of the asymmetry are pinned by
    /// <c>Parse_Env_EmptyMap_CollapsesToNullOnAServiceButIsRetainedOnADependency</c>.
    /// </remarks>
    private static Dictionary<string, string>? CollapseEmptyEnvToNull(Dictionary<string, string>? env)
        => env is { Count: > 0 } ? env : null;

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

            // 66aef95-extension fix: 'version' and 'image' are the two dependency fields whose
            // shipped schema descriptions explicitly promise "YAML's explicit null (e.g. '~')
            // parses as null and is treated identically to being absent" — GetScalarOrPlainNull
            // (unlike plain GetScalar) honours that promise for the four YAML 1.2 core-schema
            // PLAIN null tokens. See its doc comment for why this is scoped to a dedicated
            // helper rather than changing GetScalar itself.
            var version = GetScalarOrPlainNull(depMapping, "version");
            var image = GetScalarOrPlainNull(depMapping, "image");
            var security = ParseSecurity(depMapping, "Dependency", keyScalar.Value);
            var env = ParseEnvMap(depMapping, "Dependency", keyScalar.Value);

            // Collect any extra fields (everything except 'type', 'version', 'image',
            // 'security' and 'env') into a new mapping node so provider resource
            // contributors can bind them. 'security' is excluded here because it is
            // now explicitly bound above, exactly like 'image'/'version'/'type'; 'env'
            // joins them for the same reason (dependency-env REQ-001): Extra is the untyped
            // bucket for fields that no typed field claims, and 'env' now has one. (The
            // environment hash moves for every existing suite either way, because
            // DependencySpec gained an Env property that SerialiseEnvironment writes —
            // "Env":null — whether or not one was declared; that hash never leaves the
            // process, so it is not the reason for either choice.)
            //
            // The move IS a live behaviour change, on exactly the four paths that bind a
            // document WITHOUT validating it against the schema — WatchRunner.Compile,
            // ScenarioDiscovery, SuiteSetLoader, and the SHIPPED
            // Vouchfx.Sdk.Testing.ProviderTestHarness — since only those can reach a
            // dependency 'env:' at all today ($defs/dependency still refuses the key
            // outright). Harmless, and stated so the next reader need not re-derive it:
            // DependencySpec.Extra is read only by EnvironmentMapper, and only for
            // 'schemaRegistry', 'queues' and 'topics'; nothing reads an 'env' key out of it,
            // before this change or after.
            YamlMappingNode? extra = BuildExtraNode(depMapping, "type", "version", "image", "security", "env");

            dict[keyScalar.Value] = new DependencySpec(type, version, extra)
            {
                Image = image,
                Security = security,
                Env = env,
            };
        }

        return dict.Count > 0 ? dict : null;
    }

    // -------------------------------------------------------------------------
    // Security block parser (authenticated-infrastructure-mtls, PR A) — shared by
    // both ParseServiceMap and ParseDependencyMap (REQ-001 is kind-generic).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a service's or dependency's optional <c>security:</c> block (REQ-001)
    /// into a strongly-typed <see cref="SecuritySpec"/>.
    /// </summary>
    /// <param name="ownerMapping">The service's or dependency's own YAML mapping node.</param>
    /// <param name="ownerLabel">
    /// <c>"Service"</c> or <c>"Dependency"</c> — used only in diagnostic messages,
    /// mirroring the capitalised label already used by e.g. <see cref="ParseEnvMap"/>'s
    /// and <see cref="ParseDependencyMap"/>'s own throw messages.
    /// </param>
    /// <param name="ownerName">The service's or dependency's logical (map-key) name.</param>
    /// <remarks>
    /// Every field is read as its raw scalar text with NO requiredness enforced here —
    /// <c>profile</c>/<c>endpoint</c> requiredness (REQ-001/REQ-002), the
    /// mtls-requires-<c>clientCert</c>/<c>clientKey</c> rule, and the
    /// tls-forbids-<c>clientCert</c>/<c>clientKey</c> rule, and the rule that
    /// <c>clientKeyPassword</c> is a single whole <c>${secret:}</c> reference rather than a
    /// literal (client-key-password spec, REQ-001), are the JSON Schema layer's
    /// responsibility (<c>root-language-schema.json</c>'s <c>$defs/security</c>),
    /// mirroring this parser's existing "deliberately lenient" design (see this file's
    /// header remarks). <c>endpoint</c> is kept as raw scalar text rather than parsed to
    /// <see cref="int"/>: unlike <c>httpPort</c>, which always means a port number,
    /// <c>endpoint</c> may equally name a declared endpoint (a non-numeric string), so
    /// pre-parsing it here would lose that second, equally valid shape.
    /// </remarks>
    private static SecuritySpec? ParseSecurity(YamlMappingNode ownerMapping, string ownerLabel, string ownerName)
    {
        if (!TryGetMapping(ownerMapping, "security", out var securityNode))
        {
            return null;
        }

        var profile = GetScalar(securityNode, "profile");
        var endpoint = GetScalar(securityNode, "endpoint");
        var caCert = GetScalar(securityNode, "caCert");
        var clientCert = GetScalar(securityNode, "clientCert");
        var clientKey = GetScalar(securityNode, "clientKey");
        var clientKeyPassword = GetScalar(securityNode, "clientKeyPassword");
        var serverArtifacts = ParseServerArtifacts(securityNode, ownerLabel, ownerName);

        return new SecuritySpec(profile, endpoint, caCert, clientCert, clientKey, serverArtifacts)
        {
            ClientKeyPassword = clientKeyPassword,
        };
    }

    /// <summary>
    /// Parses a security block's optional <c>serverArtifacts:</c> sequence (REQ-016's
    /// authoring surface) into a list of <see cref="SecurityServerArtifactSpec"/> pairs.
    /// </summary>
    /// <exception cref="YamlParseException">
    /// Thrown when <c>serverArtifacts</c> is present but is not a sequence, or when any
    /// item is not a mapping — mirroring <see cref="ParseSeedSqlSequence"/>'s rigour for a
    /// malformed list: silently dropping a malformed entry would leave an artefact
    /// unstaged, surfacing later as a misattributed EnvironmentError (§12.1) rather than
    /// an authoring-time diagnostic.
    /// </exception>
    private static List<SecurityServerArtifactSpec>? ParseServerArtifacts(
        YamlMappingNode securityNode,
        string ownerLabel,
        string ownerName)
    {
        if (!TryGetNode(securityNode, "serverArtifacts", out var artifactsNode))
        {
            return null;
        }

        if (artifactsNode is not YamlSequenceNode sequence)
        {
            throw new YamlParseException(
                $"{ownerLabel} '{ownerName}' security 'serverArtifacts' at line {artifactsNode.Start.Line} " +
                $"must be a sequence of {{ source, target }} mappings, but found {artifactsNode.NodeType}.",
                artifactsNode.Start.Line,
                artifactsNode.Start.Column);
        }

        var list = new List<SecurityServerArtifactSpec>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode itemMapping)
            {
                throw new YamlParseException(
                    $"{ownerLabel} '{ownerName}' security 'serverArtifacts' item at line {item.Start.Line} " +
                    "must be a mapping declaring 'source' and 'target' (e.g. '{ source: ./certs/x.jks, " +
                    $"target: /etc/x.jks }}'), but found {item.NodeType}.",
                    item.Start.Line,
                    item.Start.Column);
            }

            var source = GetScalar(itemMapping, "source");
            var target = GetScalar(itemMapping, "target");
            list.Add(new SecurityServerArtifactSpec(source, target));
        }

        return list.Count > 0 ? list : null;
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
    /// Like <see cref="GetScalar"/>, but additionally resolves a dependency <c>version</c>/
    /// <c>image</c> scalar's PLAIN "no real content" spellings to <see langword="null"/>: a
    /// dangling key (no value after the colon), an explicit empty PLAIN scalar, and the four
    /// YAML 1.2 core-schema PLAIN null tokens (<c>~</c>, <c>null</c>, <c>Null</c>, <c>NULL</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every PLAIN "no real content" spelling now collapses to the SAME representation
    /// (<see langword="null"/>) — before this helper existed, a dangling key round-tripped as ""
    /// while a fully-absent key round-tripped as null: two spellings of "absent" for the one
    /// typed field. That IS a claim about PLAIN spellings only, not a global guarantee: a QUOTED
    /// empty scalar (<c>image: ""</c>) is a genuinely DIFFERENT, deliberately untouched case (see
    /// the PLAIN-style discussion below) and still returns "" verbatim — so the typed model does
    /// NOT have a single representation of "absent" for every possible authored value, only for
    /// every PLAIN one. (EnvironmentMapper's own <c>IsNullOrEmpty</c> guard is what makes a
    /// QUOTED "" behave as absent too, at the mapping layer — see its comment for why that guard
    /// is load-bearing for real authored YAML, not merely a defensive fallback.) Two spellings of
    /// "absent" is a trap regardless: a future consumer written as <c>spec.Image is not null</c>
    /// would silently treat a PLAIN dangling key's "" as present — precisely the shape of bug
    /// this file's history exists to close, which is why collapsing the PLAIN spellings matters
    /// even though it does not reach the QUOTED one. Deliberately a dedicated helper rather than
    /// a change to <see cref="GetScalar"/> itself: <see cref="GetScalar"/> feeds many callers
    /// (metadata, seed fixtures, step fields, env values, …) whose behaviour for a dangling key
    /// or a plain null-shaped value must NOT change here — this fix is scoped exactly to the two
    /// dependency fields whose shipped schema descriptions promise the treated-as-absent contract
    /// for YAML's explicit null.
    /// </para>
    /// <para>
    /// PLAIN style only — <see cref="YamlScalarNode.Style"/> distinguishes a plain scalar from a
    /// quoted one, and only plain style is ever collapsed to null. An author who explicitly
    /// quotes the value (<c>version: "~"</c>, <c>image: '~'</c>, <c>image: ""</c>) means the
    /// literal text, not YAML's null, and gets it back unchanged (confirmed empirically:
    /// <see cref="YamlScalarNode.Style"/> is <c>DoubleQuoted</c>/<c>SingleQuoted</c> for every
    /// quote style YamlDotNet's representation model produces, never <c>Plain</c>). This is the
    /// FIRST place quoting changes a dependency scalar's meaning on this parser's surface — every
    /// OTHER scalar it reads (e.g. a plain <c>version: 16</c>, which round-trips as text either
    /// quoted or not) is read back as text regardless of quoting, because the engine always wants
    /// a string there. There is no existing "quoting changes meaning" precedent being followed
    /// here; this helper establishes the first one.
    /// </para>
    /// <para>
    /// PLAIN style alone is not sufficient, though: a scalar can be Plain-styled yet carry an
    /// EXPLICIT YAML tag overriding its type — e.g. <c>image: !!str null</c> (force this to be a
    /// string) or <c>image: !!null y</c> (force this to be null, regardless of its text).
    /// Confirmed empirically, <see cref="YamlNode.Tag"/>'s <c>IsEmpty</c> is <see langword=
    /// "true"/> only when the author wrote no explicit tag at all; both <c>!!str null</c> and
    /// <c>!!null y</c> report a specific, non-empty tag instead. This helper only collapses a
    /// non-specifically-tagged (<c>Tag.IsEmpty</c>) plain scalar, so <c>!!str null</c> correctly
    /// stays the literal text <c>"null"</c> — the author's explicit <c>!!str</c> is respected.
    /// This is NOT full YAML 1.2 core-schema tag resolution, though: <c>!!null y</c> stays the
    /// literal text <c>"y"</c> rather than resolving to null, correctly resolving which would
    /// mean resolving the null tag independently of content — this helper does not attempt that.
    /// This is a defensive parser-API detail, not an author-visible gap: confirmed empirically,
    /// <c>SchemaResources.ConvertYamlToJsonDocument</c> (the YAML→JSON step schema validation
    /// runs, BEFORE this parser ever sees the document) rejects <c>!!null y</c> outright —
    /// <c>DocumentValidator.Validate</c> returns <c>IsValid: false</c> with "Encountered an
    /// unresolved tag 'tag:yaml.org,2002:null'". A document containing <c>!!null y</c> therefore
    /// never reaches this method in the shipped pipeline. <c>!!str null</c>, by contrast, DOES
    /// validate and IS reachable, which is why only that case gets the fix above — with one
    /// measured oddity worth recording: on the schema-validation path that same conversion
    /// renders <c>!!str null</c> as JSON <c>null</c> (the tag is not honoured there), so the
    /// document validates via the field's <c>["string","null"]</c> type union while this parser
    /// keeps the literal text <c>"null"</c>. The two layers disagree about the value; the engine
    /// then rejects the literal <c>"null"</c> loudly via the M3 tagless-image rule, so the
    /// disagreement never produces silent behaviour.
    /// </para>
    /// </remarks>
    private static string? GetScalarOrPlainNull(YamlMappingNode mapping, string key)
    {
        if (!TryGetNode(mapping, key, out var node) || node is not YamlScalarNode scalar)
        {
            return null;
        }

        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain &&
            scalar.Tag.IsEmpty &&
            IsPlainNullToken(scalar.Value))
        {
            return null;
        }

        return scalar.Value;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a PLAIN scalar's "no real
    /// content" spelling: the empty string (a dangling key's value), or one of the four YAML 1.2
    /// core-schema spellings of the null scalar — <c>~</c>, <c>null</c>, <c>Null</c>, or
    /// <c>NULL</c>. The four explicit spellings are exact, case-SENSITIVE matches only, matching
    /// the DSL's existing exact-case convention for other vocabulary terms (dependency
    /// <c>type</c>, <c>imagePullPolicy</c>, <c>verifyMode</c>) — an author who types e.g.
    /// <c>NuLL</c> gets that literal text back, not YAML's null. Only ever called on a scalar
    /// already confirmed to be PLAIN style with no explicit tag (see
    /// <see cref="GetScalarOrPlainNull"/>), so neither a quoted <c>"~"</c> nor an explicitly
    /// tagged <c>!!str null</c> ever reaches this check.
    /// </summary>
    private static bool IsPlainNullToken(string? value) =>
        value is "" or "~" or "null" or "Null" or "NULL";

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
