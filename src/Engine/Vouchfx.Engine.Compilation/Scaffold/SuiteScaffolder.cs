// Vouchfx.Engine.Compilation — SuiteScaffolder (Spec B / mcp-generator-scaffold-and-run).
//
// Deterministic, catalogue-driven emission of a schema-valid .e2e.yaml skeleton.
// No LLM, no timestamps, no secret literals. CLI and MCP share this library so they
// cannot drift (REQ-001..006, EDGE-001..005, REQ-010).

using System.Globalization;
using System.Text;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Compilation.Scaffold;

/// <summary>
/// Generates a machine-drafted, schema-valid <c>.e2e.yaml</c> document from a structured
/// <see cref="ScaffoldIntent"/> and a frozen <see cref="StepKindRegistry"/>.
/// </summary>
public static class SuiteScaffolder
{
    /// <summary>Default OCI image for scaffolded services.</summary>
    public const string DefaultServiceImage = "traefik/whoami";

    /// <summary>Fallback logical target when no service/dependency name is available.</summary>
    public const string FallbackTargetName = "scaffold-target";

    /// <summary>Secret reference used for any credential-shaped required field.</summary>
    public const string ScaffoldSecretReference = "${secret:env/SCAFFOLD_PLACEHOLDER}";

    /// <summary>
    /// Generates a single <c>.e2e.yaml</c> document as text.
    /// </summary>
    /// <param name="registry">Frozen provider registry (same source as catalogue / validate).</param>
    /// <param name="intent">Structured step + environment outline (not free text).</param>
    /// <param name="engineVersion">
    /// Optional engine / host version stamped into the provenance comment only
    /// (never into a language field). May be null.
    /// </param>
    /// <returns>Deterministic YAML text including the provenance comment header.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="registry"/> or <paramref name="intent"/> is null.
    /// </exception>
    /// <exception cref="ScaffoldException">
    /// Thrown on empty steps, duplicate ids, unknown step types, or unknown dependency kinds.
    /// </exception>
    public static string Generate(
        StepKindRegistry registry,
        ScaffoldIntent intent,
        string? engineVersion = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(intent);

        ValidateIntent(registry, intent);

        var catalogue = EngineExport.BuildCatalogue(registry, engineVersion);
        var byType = catalogue.StepTypes.ToDictionary(
            e => e.Type,
            e => e,
            StringComparer.Ordinal);

        var services = intent.Services ?? Array.Empty<ScaffoldServiceIntent>();
        var dependencies = intent.Dependencies ?? Array.Empty<ScaffoldDependencyIntent>();
        var targetResolver = new TargetResolver(services, dependencies);

        var sb = new StringBuilder(capacity: 2048);
        AppendProvenance(sb, engineVersion);
        AppendMetadata(sb);
        AppendEnvironment(sb, services, dependencies);
        AppendSteps(sb, intent.Steps, byType, targetResolver);
        return sb.ToString();
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void ValidateIntent(StepKindRegistry registry, ScaffoldIntent intent)
    {
        if (intent.Steps is null || intent.Steps.Count == 0)
        {
            throw new ScaffoldException(
                "Scaffold requires at least one step; the steps list is empty.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var unknownTypes = new List<string>();

        for (var i = 0; i < intent.Steps.Count; i++)
        {
            var step = intent.Steps[i]
                ?? throw new ScaffoldException($"Step at index {i} is null.");

            if (string.IsNullOrWhiteSpace(step.Id))
            {
                throw new ScaffoldException(
                    $"Step at index {i} has an empty id.");
            }

            if (!seenIds.Add(step.Id))
            {
                throw new ScaffoldException(
                    $"Duplicate step id '{step.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(step.Type))
            {
                throw new ScaffoldException(
                    $"Step '{step.Id}' has an empty type.");
            }

            if (!registry.TryGet(step.Type, out _))
                unknownTypes.Add(step.Type);
        }

        if (unknownTypes.Count > 0)
        {
            var distinct = unknownTypes.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal);
            throw new ScaffoldException(
                "Unknown step type(s) not present in the current registration: "
                + string.Join(", ", distinct.Select(t => $"'{t}'"))
                + ".");
        }

        if (intent.Services is not null)
        {
            var seenServices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var svc in intent.Services)
            {
                if (svc is null)
                    throw new ScaffoldException("A service entry is null.");
                if (string.IsNullOrWhiteSpace(svc.Name))
                    throw new ScaffoldException("A service entry has an empty name.");
                if (!seenServices.Add(svc.Name))
                    throw new ScaffoldException($"Duplicate service name '{svc.Name}'.");
            }
        }

        if (intent.Dependencies is not null)
        {
            var seenDeps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dep in intent.Dependencies)
            {
                if (dep is null)
                    throw new ScaffoldException("A dependency entry is null.");
                if (string.IsNullOrWhiteSpace(dep.Name))
                    throw new ScaffoldException("A dependency entry has an empty name.");
                if (!seenDeps.Add(dep.Name))
                    throw new ScaffoldException($"Duplicate dependency name '{dep.Name}'.");
                if (string.IsNullOrWhiteSpace(dep.Type))
                {
                    throw new ScaffoldException(
                        $"Dependency '{dep.Name}' has an empty type.");
                }

                if (!KnownDependencyKinds.Contains(dep.Type))
                {
                    throw new ScaffoldException(
                        $"Unsupported dependency type '{dep.Type}' for dependency '{dep.Name}'. "
                        + $"Supported types: {string.Join(", ", KnownDependencyKinds.All)}.");
                }
            }
        }
    }

    // ── Emission ──────────────────────────────────────────────────────────────

    private static void AppendProvenance(StringBuilder sb, string? engineVersion)
    {
        // No timestamps — EDGE-005 requires identical output for identical inputs.
        var versionPart = string.IsNullOrWhiteSpace(engineVersion)
            ? "vouchfx scaffold"
            : $"vouchfx scaffold (engine {engineVersion.Trim()})";

        sb.Append("# Machine-drafted by ").Append(versionPart).AppendLine(".");
        sb.AppendLine(
            "# Generated skeleton grounded in the live step catalogue — human review required before trust.");
        sb.AppendLine(
            "# Placeholders only; fill semantics before treating this document as production-ready.");
        sb.AppendLine();
    }

    private static void AppendMetadata(StringBuilder sb)
    {
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: scaffolded-suite");
        sb.AppendLine("  tags: [scaffolded]");
        sb.AppendLine(
            "  description: Machine-drafted suite skeleton from vouchfx scaffold; review and fill before trust.");
        sb.AppendLine();
    }

    private static void AppendEnvironment(
        StringBuilder sb,
        IReadOnlyList<ScaffoldServiceIntent> services,
        IReadOnlyList<ScaffoldDependencyIntent> dependencies)
    {
        if (services.Count == 0 && dependencies.Count == 0)
            return;

        sb.AppendLine("environment:");

        if (services.Count > 0)
        {
            sb.AppendLine("  services:");
            foreach (var svc in services)
            {
                var image = string.IsNullOrWhiteSpace(svc.Image)
                    ? DefaultServiceImage
                    : svc.Image.Trim();
                sb.Append("    ").Append(EscapeKey(svc.Name)).AppendLine(":");
                sb.Append("      image: ").AppendLine(QuoteIfNeeded(image));
                sb.AppendLine("      httpPort: 80");
            }
        }

        if (dependencies.Count > 0)
        {
            sb.AppendLine("  dependencies:");
            foreach (var dep in dependencies)
            {
                sb.Append("    ").Append(EscapeKey(dep.Name)).AppendLine(":");
                // Emit the kind as supplied (preserve author casing when legal; mapper is ignore-case).
                sb.Append("      type: ").AppendLine(QuoteIfNeeded(dep.Type.Trim()));
            }
        }

        sb.AppendLine();
    }

    private static void AppendSteps(
        StringBuilder sb,
        IReadOnlyList<ScaffoldStepIntent> steps,
        IReadOnlyDictionary<string, StepCatalogueEntry> byType,
        TargetResolver targets)
    {
        sb.AppendLine("steps:");
        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Label))
            {
                // Labels are comments only (not a language field).
                sb.Append("  # label: ").AppendLine(step.Label.Trim());
            }

            var entry = byType[step.Type];
            sb.AppendLine("  - id: " + QuoteIfNeeded(step.Id));
            sb.AppendLine("    type: " + QuoteIfNeeded(step.Type));

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in entry.RequiredFields)
            {
                EmitField(sb, field, step.Type, entry.Family, entry.Provider, targets, indent: "    ");
                emitted.Add(field);
            }

            // Catalogue oneOf shapes (script.csharp) and provider-validator companions that
            // are not always listed in fragment "required" still need placeholders so the
            // full validate pipeline (schema + bind + Validate) succeeds.
            EmitCompanionFields(sb, step.Type, entry.Family, entry.Provider, targets, emitted);
        }
    }

    private static void EmitCompanionFields(
        StringBuilder sb,
        string stepType,
        string family,
        string provider,
        TargetResolver targets,
        HashSet<string> emitted)
    {
        // script.csharp: fragment uses oneOf [code|file]; catalogue required is empty.
        if (string.Equals(stepType, "script.csharp", StringComparison.Ordinal)
            && !emitted.Contains("code")
            && !emitted.Contains("file"))
        {
            EmitScalar(sb, "code", "// scaffold", indent: "    ");
            emitted.Add("code");
        }

        // Azure Service Bus: queue / expect payload are enforced by IStepValidator, not
        // always by the fragment's top-level required array.
        if (string.Equals(provider, "azureservicebus", StringComparison.Ordinal))
        {
            if (!emitted.Contains("queue") && !emitted.Contains("topic"))
            {
                EmitScalar(sb, "queue", "scaffold-queue", indent: "    ");
                emitted.Add("queue");
            }

            if (string.Equals(family, "mq-expect", StringComparison.Ordinal)
                && !emitted.Contains("expectPayloadContains")
                && !emitted.Contains("expectProperties"))
            {
                EmitScalar(sb, "expectPayloadContains", "scaffold", indent: "    ");
                emitted.Add("expectPayloadContains");
            }
        }
    }

    private static void EmitField(
        StringBuilder sb,
        string field,
        string stepType,
        string family,
        string provider,
        TargetResolver targets,
        string indent)
    {
        if (IsSecretLikeField(field))
        {
            EmitScalar(sb, field, ScaffoldSecretReference, indent);
            return;
        }

        switch (field)
        {
            case "target":
                EmitScalar(sb, "target", targets.ResolveTarget(stepType, family, provider), indent);
                return;
            case "listener":
                EmitScalar(sb, "listener", "scaffold-listener", indent);
                return;
            case "receiver":
                EmitScalar(sb, "receiver", "scaffold-receiver", indent);
                return;
            case "method":
                EmitScalar(sb, "method", "GET", indent);
                return;
            case "path":
                EmitScalar(sb, "path", "/", indent);
                return;
            case "query":
                EmitQuery(sb, family, provider, indent);
                return;
            case "envelope":
                EmitScalar(
                    sb,
                    "envelope",
                    "<Envelope xmlns=\"http://schemas.xmlsoap.org/soap/envelope/\"><Body><Scaffold/></Body></Envelope>",
                    indent);
                return;
            case "collection":
                EmitScalar(sb, "collection", "scaffold", indent);
                return;
            case "filter":
                EmitScalar(sb, "filter", "{}", indent);
                return;
            case "table":
                EmitScalar(sb, "table", "scaffold", indent);
                return;
            case "key":
                EmitKey(sb, stepType, family, indent);
                return;
            case "operation":
                // Pair with expect.exists for cache-assert.redis.
                EmitScalar(sb, "operation", "exists", indent);
                return;
            case "index":
                EmitScalar(sb, "index", "scaffold", indent);
                return;
            case "topic":
                EmitScalar(sb, "topic", "scaffold-topic", indent);
                return;
            case "subject":
                EmitScalar(sb, "subject", "scaffold.subject", indent);
                return;
            case "queue":
                EmitScalar(sb, "queue", "scaffold-queue", indent);
                return;
            case "stream":
                EmitScalar(sb, "stream", "scaffold-stream", indent);
                return;
            case "routingKey":
                EmitScalar(sb, "routingKey", "scaffold", indent);
                return;
            case "payload":
                EmitScalar(sb, "payload", "scaffold-payload", indent);
                return;
            case "code":
                EmitScalar(sb, "code", "// scaffold", indent);
                return;
            case "file":
                EmitScalar(sb, "file", "scaffold.csx", indent);
                return;
            case "metric":
                EmitScalar(sb, "metric", "scaffold_metric", indent);
                return;
            case "bucket":
                EmitScalar(sb, "bucket", "scaffold-bucket", indent);
                return;
            case "channel":
                EmitScalar(sb, "channel", "scaffold-channel", indent);
                return;
            case "subscription":
                EmitScalar(sb, "subscription", "scaffold-subscription", indent);
                return;
            case "expect":
                EmitExpect(sb, stepType, family, provider, indent);
                return;
            case "match":
                EmitMatch(sb, stepType, family, indent);
                return;
            default:
                // Unknown required field: deterministic string placeholder (never a secret literal).
                EmitScalar(sb, field, "scaffold", indent);
                return;
        }
    }

    private static void EmitQuery(StringBuilder sb, string family, string provider, string indent)
    {
        // Elasticsearch optional query is JSON; SQL families use a SELECT.
        if (string.Equals(family, "cache-assert", StringComparison.Ordinal)
            && string.Equals(provider, "elasticsearch", StringComparison.Ordinal))
        {
            EmitScalar(sb, "query", "{\"query\":{\"match_all\":{}}}", indent);
            return;
        }

        EmitScalar(sb, "query", "SELECT 1 AS scaffold", indent);
    }

    private static void EmitKey(StringBuilder sb, string stepType, string family, string indent)
    {
        if (string.Equals(stepType, "db-assert.dynamodb", StringComparison.Ordinal))
        {
            EmitScalar(sb, "key", "{\"id\":\"scaffold\"}", indent);
            return;
        }

        if (string.Equals(family, "storage-assert", StringComparison.Ordinal))
        {
            EmitScalar(sb, "key", "scaffold-object", indent);
            return;
        }

        EmitScalar(sb, "key", "scaffold-key", indent);
    }

    private static void EmitExpect(
        StringBuilder sb,
        string stepType,
        string family,
        string provider,
        string indent)
    {
        sb.Append(indent).AppendLine("expect:");
        var nested = indent + "  ";

        if (string.Equals(family, "db-assert", StringComparison.Ordinal))
        {
            if (string.Equals(provider, "mongodb", StringComparison.Ordinal))
            {
                sb.Append(nested).AppendLine("count: 0");
                return;
            }

            if (string.Equals(provider, "dynamodb", StringComparison.Ordinal))
            {
                sb.Append(nested).AppendLine("exists: false");
                return;
            }

            // postgres / mysql / sqlserver
            sb.Append(nested).AppendLine("rowCount: 0");
            return;
        }

        if (string.Equals(family, "cache-assert", StringComparison.Ordinal))
        {
            if (string.Equals(provider, "redis", StringComparison.Ordinal))
            {
                // Pairs with operation: exists.
                sb.Append(nested).AppendLine("exists: false");
                return;
            }

            if (string.Equals(provider, "elasticsearch", StringComparison.Ordinal))
            {
                sb.Append(nested).AppendLine("count: 0");
                return;
            }
        }

        if (string.Equals(family, "mail-expect", StringComparison.Ordinal))
        {
            sb.Append(nested).AppendLine("match:");
            sb.Append(nested).Append("  ").AppendLine("subject-contains: scaffold");
            return;
        }

        if (string.Equals(family, "metrics-assert", StringComparison.Ordinal))
        {
            sb.Append(nested).AppendLine("value: 0");
            return;
        }

        if (string.Equals(family, "storage-assert", StringComparison.Ordinal))
        {
            sb.Append(nested).AppendLine("exists: false");
            return;
        }

        // Generic minimal object so schema "required: [expect]" is satisfied.
        sb.Append(nested).AppendLine("scaffold: true");
    }

    private static void EmitMatch(
        StringBuilder sb,
        string stepType,
        string family,
        string indent)
    {
        sb.Append(indent).AppendLine("match:");
        var nested = indent + "  ";

        if (string.Equals(family, "trace-expect", StringComparison.Ordinal))
        {
            // Deterministic 32-hex trace id (validator requires non-empty traceId).
            sb.Append(nested).AppendLine("traceId: \"00000000000000000000000000000001\"");
            return;
        }

        if (string.Equals(family, "webhook-listen", StringComparison.Ordinal))
        {
            sb.Append(nested).AppendLine("method: POST");
            return;
        }

        // mq-expect.* and similar: at least one criterion.
        sb.Append(nested).AppendLine("payloadContains: scaffold");
    }

    private static void EmitScalar(StringBuilder sb, string name, string value, string indent)
    {
        sb.Append(indent).Append(name).Append(": ").AppendLine(QuoteIfNeeded(value));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when a field name is credential-shaped and must receive a
    /// <c>${secret:…}</c> reference rather than any literal.
    /// </summary>
    internal static bool IsSecretLikeField(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
            return false;

        var n = fieldName.ToLowerInvariant();
        return n.Contains("secret", StringComparison.Ordinal)
            || n.Contains("password", StringComparison.Ordinal)
            || n.Contains("token", StringComparison.Ordinal)
            || n.Contains("authorization", StringComparison.Ordinal)
            || n.Contains("apikey", StringComparison.Ordinal)
            || n.Contains("api_key", StringComparison.Ordinal)
            || n.Contains("api-key", StringComparison.Ordinal)
            || n.Contains("accesskey", StringComparison.Ordinal)
            || n.Contains("secretkey", StringComparison.Ordinal);
    }

    private static string EscapeKey(string key)
    {
        // YAML plain map keys used as service/dependency names — quote if needed.
        return QuoteIfNeeded(key);
    }

    /// <summary>
    /// Quotes a scalar when it would be ambiguous as a plain YAML token.
    /// Deterministic: same input always same quoting.
    /// </summary>
    internal static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        // Always double-quote when special YAML characters or leading/trailing space.
        var needsQuote = false;
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            needsQuote = true;
        else
        {
            foreach (var ch in value)
            {
                if (ch is ':' or '#' or '{' or '}' or '[' or ']' or ',' or '&' or '*'
                    or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`'
                    or '?' or '\n' or '\r' or '\t')
                {
                    needsQuote = true;
                    break;
                }
            }

            // Leading indicators that YAML would mis-parse.
            if (!needsQuote
                && (value.StartsWith('-')
                    || value.StartsWith('?')
                    || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)))
            {
                needsQuote = true;
            }

            // Pure numbers would become numeric tokens.
            if (!needsQuote
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                needsQuote = true;
            }
        }

        if (!needsQuote)
            return value;

        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            + "\"";
    }

    /// <summary>
    /// Resolves a step's <c>target</c> from the environment outline using family/provider hints.
    /// </summary>
    private sealed class TargetResolver
    {
        private readonly IReadOnlyList<ScaffoldServiceIntent> _services;
        private readonly IReadOnlyList<ScaffoldDependencyIntent> _dependencies;

        public TargetResolver(
            IReadOnlyList<ScaffoldServiceIntent> services,
            IReadOnlyList<ScaffoldDependencyIntent> dependencies)
        {
            _services = services;
            _dependencies = dependencies;
        }

        public string ResolveTarget(string stepType, string family, string provider)
        {
            // HTTP / metrics hit services.
            if (string.Equals(family, "http", StringComparison.Ordinal)
                || string.Equals(family, "metrics-assert", StringComparison.Ordinal))
            {
                if (_services.Count > 0)
                    return _services[0].Name;
                if (_dependencies.Count > 0)
                    return _dependencies[0].Name;
                return FallbackTargetName;
            }

            // Prefer a dependency whose kind matches the provider / family convention.
            var preferredKinds = PreferredDependencyKinds(stepType, family, provider);
            foreach (var kind in preferredKinds)
            {
                foreach (var dep in _dependencies)
                {
                    if (string.Equals(dep.Type, kind, StringComparison.OrdinalIgnoreCase))
                        return dep.Name;
                }
            }

            if (_dependencies.Count > 0)
                return _dependencies[0].Name;
            if (_services.Count > 0)
                return _services[0].Name;
            return FallbackTargetName;
        }

        private static string[] PreferredDependencyKinds(
            string stepType,
            string family,
            string provider)
        {
            // Provider id often matches the dependency kind (postgres, kafka, redis, …).
            // Special cases where they diverge.
            _ = stepType;
            if (string.Equals(provider, "smtp", StringComparison.Ordinal))
                return new[] { "mailpit" };
            if (string.Equals(provider, "s3", StringComparison.Ordinal)
                || string.Equals(family, "storage-assert", StringComparison.Ordinal))
                return new[] { "minio" };
            if (string.Equals(provider, "azureservicebus", StringComparison.Ordinal))
                return new[] { "azureservicebus" };

            // Family-level fallbacks when provider is not a dep kind.
            if (KnownDependencyKinds.Contains(provider))
                return new[] { provider };

            return family switch
            {
                "db-assert" => new[] { "postgres", "mysql", "sqlserver", "mongodb", "dynamodb" },
                "mq-publish" or "mq-expect" => new[]
                {
                    "kafka", "rabbitmq", "nats", "azureservicebus", "redis",
                },
                "cache-assert" => new[] { "redis", "elasticsearch" },
                "mail-expect" => new[] { "mailpit" },
                "storage-assert" => new[] { "minio" },
                _ => Array.Empty<string>(),
            };
        }
    }
}
