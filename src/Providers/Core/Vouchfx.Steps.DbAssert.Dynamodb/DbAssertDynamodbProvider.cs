// Vouchfx.Steps.DbAssert.Dynamodb — db-assert.dynamodb step provider (DSL §5, §13.10).
//
// Executes a GetItem call against a declared DynamoDB dependency (dynamodb-local, provisioned
// as the "dynamodb" dependency type — see EnvironmentMapper) and asserts on item existence
// and/or individual top-level attribute values.
//
// Connection-string form (produced by EnvironmentMapper's "dynamodb" registration; §17 n/a —
// dynamodb-local ignores credentials entirely, so these are fixed dummy values, never secrets):
//   ServiceURL=http://<host>:<port>;AccessKey=local;SecretKey=local
// ParseConnectionString (in the emitted helper) splits this key=value;… form and builds an
// AmazonDynamoDBConfig { ServiceURL } + BasicAWSCredentials pair.
//
// Key JSON shape (v1, deliberately simplified — NOT the raw low-level DynamoDB attribute-value
// wire form): a flat JSON object naming the primary key (partition [+ sort]), e.g.
// {"orderId":"1001"}. Each top-level JSON value becomes a DynamoDB attribute at execution time:
// a JSON string → S, a JSON number → N (DynamoDB's own decimal-string form), a JSON boolean →
// BOOL. Nested objects/arrays/null are rejected (a primary key cannot contain them).
//
// Item-attribute stringification (for expect.item comparison, top-level only — nested M/L/SS/
// NS/BS attributes are out of v1 scope): S as-is; N as DynamoDB's raw number string (e.g. "42"
// or "42.5", never re-formatted); BOOL as the literal "true"/"false"; any other/unsupported
// attribute type or a missing field renders as the literal "null" (a definite non-match against
// any author-declared expected string, never a silent skip).
//
// Injection safety (JSON breakout via {placeholder} substitution — replicates
// DbAssertMongodbProvider's ResolveFilter hardening exactly, applied to the key template
// instead of a query filter):
//   • The key field accepts a JSON template with {placeholder} tokens.
//   • At runtime inside ExecuteAsync, ResolveKeyTemplate JSON-escapes each resolved placeholder
//     value (System.Text.Json.JsonSerializer.Serialize) before splicing it into the key JSON.
//     A value containing a literal '"' cannot therefore break out of its enclosing JSON string
//     and inject additional key attributes. Placeholders OUTSIDE a JSON string literal are not
//     supported (authors must quote them, exactly as db-assert.mongodb requires).
//   • expect.item expected values support arbitrary substitution via Substitute_Helpers.Resolve.
//
// Verdict taxonomy (§12.1):
//   • conn:: key absent from Vars                              → EnvironmentError.
//   • {placeholder}/JSON errors resolving the key template      → EnvironmentError.
//   • any AWS SDK / connection exception (client build, network,
//     table not found, credentials, etc.)                       → EnvironmentError. Exception
//     TYPE names only are emitted (never ex.Message) — the AWS SDK's own exception messages can
//     embed the ServiceURL, mirroring the mail-expect.smtp / RetryRunner pattern of emitting
//     ex.GetType().Name rather than the raw message (§17 spirit: no infrastructure detail leaks
//     into the author-visible observation).
//   • item absent when expect.exists is true (the default)      → Fail, {"exists":false}.
//   • item present when expect.exists is explicitly false       → Fail, {"exists":true}.
//   • an expect.item field mismatch                             → Fail,
//     {"field":…,"expected":…,"actual":…} (mirrors db-assert.mongodb's diff shape exactly, so
//     the same table-rendering IStepDiffRenderer logic applies unchanged).
//   • otherwise                                                 → Pass, {"exists":true|false}.
//
// GetItem is a read-only, idempotent call, so this provider fully supports verifyMode: RETRY
// with no special-case code — the engine-owned RetryRunner re-invokes the emitted statement
// block on each poll attempt (§7); the provider need only emit an idempotent single-attempt
// fragment, exactly as every other db-assert provider does.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers contains
//     the full provider-id-prefixed static class definition; StatementBlock is a C# 11
//     $$"""…""" block; 'using var' is illegal (the helper below uses plain 'var' plus explicit
//     .Dispose()/JsonDocument disposal in 'finally', mirroring every other provider).
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.DbAssert.Dynamodb;

/// <summary>
/// Core provider for the <c>db-assert.dynamodb</c> step kind (DSL §5). Executes a GetItem call
/// against a declared DynamoDB dependency and asserts on item existence and/or individual
/// top-level attribute values.
/// </summary>
[StepProvider]
public sealed class DbAssertDynamodbProvider
    : IStepProvider,
      IStepBinder<DbAssertDynamodbModel>,
      IStepValidator<DbAssertDynamodbModel>,
      IStepCompiler<DbAssertDynamodbModel>,
      IResourceContributor<DbAssertDynamodbModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("db-assert", "dynamodb");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<DbAssertDynamodbModel> ────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "table", "key", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the dynamodb dependency to query, as declared under environment.dependencies.",
              "type": "string"
            },
            "table": {
              "description": "Name of the DynamoDB table to query. May contain {placeholder} tokens.",
              "type": "string"
            },
            "key": {
              "description": "A flat JSON object template naming the primary key (partition key, optionally plus a sort key) for a GetItem call, e.g. {\"orderId\":\"{orderId}\"}. Each top-level value becomes an S/N/BOOL DynamoDB attribute; nested objects/arrays/null are not supported. May contain {placeholder} tokens inside JSON string values.",
              "type": "string"
            },
            "expect": {
              "description": "Assertion block declaring the expected GetItem outcome.",
              "type": "object",
              "properties": {
                "exists": {
                  "description": "Whether the item is expected to exist. Defaults to true when omitted.",
                  "type": "boolean"
                },
                "item": {
                  "description": "Map of flat (top-level) attribute name to expected string value, compared against the GetItem result (S as-is, N as the raw number string, BOOL as \"true\"/\"false\"). Nested attributes are not supported in v1.",
                  "type": "object",
                  "additionalProperties": { "type": "string" }
                }
              },
              "additionalProperties": false
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public DbAssertDynamodbModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new DbAssertDynamodbModel(
                Target: string.Empty,
                Table: string.Empty,
                Key: string.Empty,
                Expect: new DynamoExpectation(Exists: null, Item: null));
        }

        var target = GetScalar(mapping, "target");
        var table = GetScalar(mapping, "table");
        var key = GetScalar(mapping, "key");

        bool? exists = null;
        IReadOnlyDictionary<string, string>? item = null;

        if (mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            && expectNode is YamlMappingNode expectMap)
        {
            if (expectMap.Children.TryGetValue(new YamlScalarNode("exists"), out var existsNode)
                && existsNode is YamlScalarNode existsScalar
                && bool.TryParse(existsScalar.Value, out var parsedExists))
            {
                exists = parsedExists;
            }

            if (expectMap.Children.TryGetValue(new YamlScalarNode("item"), out var itemNode)
                && itemNode is YamlMappingNode itemMap)
            {
                var itemDict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in itemMap.Children)
                {
                    if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                        itemDict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
                }
                item = itemDict;
            }
        }

        return new DbAssertDynamodbModel(
            Target: target,
            Table: table,
            Key: key,
            Expect: new DynamoExpectation(Exists: exists, Item: item));
    }

    // ── IStepValidator<DbAssertDynamodbModel> ─────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(DbAssertDynamodbModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("db-assert.dynamodb: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Table))
            errors.Add("db-assert.dynamodb: 'table' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Key))
            errors.Add("db-assert.dynamodb: 'key' must not be empty.");

        // Best-effort key-shape check: if the key parses as JSON (it may not, if it carries a
        // {placeholder} token outside a quoted string), every top-level value must be a
        // string/number/boolean — a primary key cannot contain nested objects/arrays/null.
        // A parse failure is deferred to runtime (ExecuteAsync validates the RESOLVED key).
        if (!string.IsNullOrWhiteSpace(model.Key))
        {
            try
            {
                using var doc = JsonDocument.Parse(model.Key);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("db-assert.dynamodb: 'key' must be a JSON object.");
                }
                else
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number
                            or JsonValueKind.True or JsonValueKind.False))
                        {
                            errors.Add(
                                $"db-assert.dynamodb: 'key' attribute '{prop.Name}' must be a JSON " +
                                "string, number, or boolean (nested/array/null values are not " +
                                "supported for a primary key).");
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Contains a {placeholder} token or other non-parseable content; ExecuteAsync
                // re-validates the resolved key at runtime.
            }
        }

        // Coherence: exists:false excludes item-field expectations — asserting both "the item
        // is absent" and "its fields look like this" is contradictory.
        if (model.Expect.Exists == false && model.Expect.Item is { Count: > 0 })
        {
            errors.Add(
                "db-assert.dynamodb: 'expect.exists: false' cannot be combined with " +
                "'expect.item' — an absent item has no fields to assert on.");
        }

        // Dependency reconciliation: target must name a declared dynamodb dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"db-assert.dynamodb: 'target' '{model.Target}' is not a " +
                    "dynamodb dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "dynamodb", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"db-assert.dynamodb: 'target' '{model.Target}' is not a " +
                    "dynamodb dependency declared in environment.dependencies.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Collections.Generic",
        "System.Diagnostics",
        "System.Threading.Tasks",
        "Amazon.DynamoDBv2",
        "Amazon.DynamoDBv2.Model",
        "Amazon.Runtime",
        "Vouchfx.Engine.Abstractions",
    };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1). Byte-identical across
    /// every step instance of this provider within a suite — no per-step interpolation.
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        """
        static class DbAssertDynamodb_Helpers
        {
            private static readonly System.Text.RegularExpressions.Regex _placeholderRegex =
                new System.Text.RegularExpressions.Regex("{([A-Za-z_][A-Za-z0-9_]*)}");

            /// <summary>
            /// Resolves {placeholder} tokens in a JSON key template. Each resolved value is
            /// JSON-escaped before splicing to prevent breaking out of the enclosing JSON
            /// string (e.g. a value containing a literal '"' cannot inject extra key
            /// attributes). Placeholders not found in vars are kept as literal text.
            /// </summary>
            public static string ResolveKeyTemplate(
                System.Collections.Generic.IDictionary<string, object?> vars,
                string keyTemplate)
            {
                return _placeholderRegex.Replace(keyTemplate, m =>
                {
                    var name = m.Groups[1].Value;
                    if (!vars.TryGetValue(name, out var val) || val is null)
                        return m.Value;
                    var strVal = val is string sv ? sv : System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    var serialised = System.Text.Json.JsonSerializer.Serialize(strVal);
                    return serialised.Length >= 2
                        ? serialised.Substring(1, serialised.Length - 2)
                        : strVal;
                });
            }

            /// <summary>
            /// Parses the "ServiceURL=…;AccessKey=…;SecretKey=…" connection-string form
            /// EnvironmentMapper's "dynamodb" registration produces (dynamodb-local ignores
            /// credentials entirely, so AccessKey/SecretKey are always the fixed dummy values
            /// "local"/"local" for this dependency type).
            /// </summary>
            private static void ParseConnectionString(
                string connStr, out string serviceUrl, out string accessKey, out string secretKey)
            {
                serviceUrl = string.Empty;
                accessKey = string.Empty;
                secretKey = string.Empty;
                foreach (var part in connStr.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = part.Substring(0, eq).Trim();
                    var value = part.Substring(eq + 1).Trim();
                    if (string.Equals(key, "ServiceURL", System.StringComparison.OrdinalIgnoreCase))
                        serviceUrl = value;
                    else if (string.Equals(key, "AccessKey", System.StringComparison.OrdinalIgnoreCase))
                        accessKey = value;
                    else if (string.Equals(key, "SecretKey", System.StringComparison.OrdinalIgnoreCase))
                        secretKey = value;
                }
                if (string.IsNullOrEmpty(serviceUrl))
                    throw new System.InvalidOperationException("db-assert.dynamodb: connection string is missing 'ServiceURL'.");
            }

            /// <summary>
            /// Parses a resolved key JSON object into DynamoDB attribute values: a JSON string
            /// becomes S, a JSON number becomes N (the raw JSON number text — DynamoDB's own
            /// decimal-string form), a JSON boolean becomes BOOL. Nested objects/arrays/null are
            /// rejected — a primary key cannot contain them.
            /// </summary>
            private static System.Collections.Generic.Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue> BuildKeyAttributes(string keyJson)
            {
                var doc = System.Text.Json.JsonDocument.Parse(keyJson);
                try
                {
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                        throw new System.InvalidOperationException("db-assert.dynamodb: 'key' must resolve to a JSON object.");

                    var result = new System.Collections.Generic.Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>(System.StringComparer.Ordinal);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        Amazon.DynamoDBv2.Model.AttributeValue attr;
                        switch (prop.Value.ValueKind)
                        {
                            case System.Text.Json.JsonValueKind.String:
                                attr = new Amazon.DynamoDBv2.Model.AttributeValue { S = prop.Value.GetString() ?? string.Empty };
                                break;
                            case System.Text.Json.JsonValueKind.Number:
                                attr = new Amazon.DynamoDBv2.Model.AttributeValue { N = prop.Value.GetRawText() };
                                break;
                            case System.Text.Json.JsonValueKind.True:
                            case System.Text.Json.JsonValueKind.False:
                                attr = new Amazon.DynamoDBv2.Model.AttributeValue { BOOL = prop.Value.GetBoolean() };
                                break;
                            default:
                                throw new System.InvalidOperationException(
                                    "db-assert.dynamodb: key attribute '" + prop.Name + "' must resolve to a JSON string, number, or boolean.");
                        }
                        result[prop.Name] = attr;
                    }
                    return result;
                }
                finally
                {
                    doc.Dispose();
                }
            }

            /// <summary>
            /// Stringifies a top-level DynamoDB attribute for expect.item comparison: S as-is,
            /// N as the raw stored number string, BOOL as "true"/"false". Any other attribute
            /// type (M/L/SS/NS/BS/NULL — nested attributes are out of v1 scope) renders as the
            /// literal "null", a definite non-match for any declared expected string.
            /// </summary>
            private static string StringifyAttribute(Amazon.DynamoDBv2.Model.AttributeValue attr)
            {
                if (attr.S is not null)
                    return attr.S;
                if (attr.N is not null)
                    return attr.N;
                if (attr.IsBOOLSet)
                    return attr.BOOL ? "true" : "false";
                return "null";
            }

            /// <summary>
            /// Executes a GetItem call via AWSSDK.DynamoDBv2, evaluates the exists/item-field
            /// expectations, and writes a typed StepOutcome into Vars. Missing connection string
            /// or any AWS SDK/network exception = EnvironmentError (§12.1); exception TYPE names
            /// only are emitted, never the exception message (the AWS SDK's own messages can
            /// embed the ServiceURL). Existence/attribute mismatch = Fail. Otherwise = Pass.
            /// </summary>
            public static async System.Threading.Tasks.Task ExecuteAsync(
                System.Collections.Generic.IDictionary<string, object?> vars,
                string outcomeKey,
                string connKey,
                string table,
                string keyTemplate,
                bool expectExists,
                string[] expectFields,
                string[] expectValues)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Vouchfx.Engine.Abstractions.Verdict verdict;
                string observation;

                var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;
                if (string.IsNullOrEmpty(connStr))
                {
                    sw.Stop();
                    vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(
                        Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,
                        sw.ElapsedMilliseconds,
                        "{\"error\":" + System.Text.Json.JsonSerializer.Serialize("connection string not found for key '" + connKey + "'") + "}");
                    return;
                }

                string resolvedKey;
                try
                {
                    resolvedKey = ResolveKeyTemplate(vars, keyTemplate);
                }
                catch (System.Exception ex)
                {
                    sw.Stop();
                    vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(
                        Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,
                        sw.ElapsedMilliseconds,
                        "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.GetType().Name) + "}");
                    return;
                }

                Amazon.DynamoDBv2.AmazonDynamoDBClient? client = null;
                try
                {
                    ParseConnectionString(connStr, out var serviceUrl, out var accessKey, out var secretKey);
                    // MaxErrorRetry=0 + a bounded Timeout/ConnectTimeout: the engine-owned
                    // RetryRunner already owns the retry/backoff cadence for verifyMode: RETRY
                    // (§7); stacking the AWS SDK's own default retries (which can otherwise
                    // wait up to ~100s per attempt against an unreachable endpoint) underneath
                    // that would make a single poll attempt hang far longer than the step's own
                    // timeout budget. A local dynamodb-local container responds in milliseconds
                    // when healthy, so 5s is generous headroom, not a tight race.
                    var config = new Amazon.DynamoDBv2.AmazonDynamoDBConfig
                    {
                        ServiceURL = serviceUrl,
                        MaxErrorRetry = 0,
                        Timeout = System.TimeSpan.FromSeconds(5),
                        ConnectTimeout = System.TimeSpan.FromSeconds(5),
                    };
                    var creds = new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey);
                    client = new Amazon.DynamoDBv2.AmazonDynamoDBClient(creds, config);

                    var keyAttrs = BuildKeyAttributes(resolvedKey);

                    var request = new Amazon.DynamoDBv2.Model.GetItemRequest
                    {
                        TableName = table,
                        Key = keyAttrs,
                    };

                    var response = await client.GetItemAsync(request).ConfigureAwait(false);
                    var found = response.Item is not null && response.Item.Count > 0;

                    string? failObservation = null;

                    if (!found)
                    {
                        if (expectExists)
                            failObservation = "{\"exists\":false}";
                    }
                    else if (!expectExists)
                    {
                        failObservation = "{\"exists\":true}";
                    }
                    else
                    {
                        for (int i = 0; i < expectFields.Length && failObservation is null; i++)
                        {
                            var fieldName = expectFields[i];
                            var expectedVal = expectValues[i];
                            var actualVal = response.Item.TryGetValue(fieldName, out var attr)
                                ? StringifyAttribute(attr)
                                : "null";
                            if (!string.Equals(actualVal, expectedVal, System.StringComparison.Ordinal))
                            {
                                failObservation =
                                    "{\"field\":" + System.Text.Json.JsonSerializer.Serialize(fieldName) +
                                    ",\"expected\":" + System.Text.Json.JsonSerializer.Serialize(expectedVal) +
                                    ",\"actual\":" + System.Text.Json.JsonSerializer.Serialize(actualVal) + "}";
                            }
                        }
                    }

                    if (failObservation is not null)
                    {
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                        observation = failObservation;
                    }
                    else
                    {
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;
                        observation = "{\"exists\":" + (found ? "true" : "false") + "}";
                    }
                }
                catch (System.Exception ex)
                {
                    // Any AWS SDK / connection / parse failure = EnvironmentError (§12.1).
                    // Exception TYPE name only — never ex.Message (may embed the ServiceURL).
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.GetType().Name) + "}";
                }
                finally
                {
                    sw.Stop();
                    client?.Dispose();
                }

                vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(
                    verdict, sw.ElapsedMilliseconds, observation);
            }
        }
        """,
    };

    // ── IStepCompiler<DbAssertDynamodbModel> ──────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(DbAssertDynamodbModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        string[] expectFields;
        string[] expectValues;
        if (model.Expect.Item is { Count: > 0 } item)
        {
            expectFields = item.Keys.ToArray();
            expectValues = item.Values.ToArray();
        }
        else
        {
            expectFields = Array.Empty<string>();
            expectValues = Array.Empty<string>();
        }

        var expectExistsLiteral = (model.Expect.Exists ?? true) ? "true" : "false";

        var keyLiteral = JsonSerializer.Serialize(model.Key);

        // 'table' supports {placeholder} substitution at runtime (per-tenant table names etc.).
        var tableExpr = $"Substitute_Helpers.Resolve(Vars, {JsonSerializer.Serialize(model.Table)})";

        // expect.item expected VALUES resolve {placeholder} tokens at runtime.
        var resolvedExpectValues = new string[expectValues.Length];
        for (int i = 0; i < expectValues.Length; i++)
        {
            resolvedExpectValues[i] =
                $"Substitute_Helpers.Resolve(Vars, {JsonSerializer.Serialize(expectValues[i])})";
        }

        var expectFieldsLiteral = BuildStringArrayLiteral(expectFields);
        var expectValuesLiteral = BuildResolvedArrayLiteral(resolvedExpectValues);

        var block = $$"""
            {
                await DbAssertDynamodb_Helpers.ExecuteAsync(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{tableExpr}},
                    {{keyLiteral}},
                    {{expectExistsLiteral}},
                    {{expectFieldsLiteral}},
                    {{expectValuesLiteral}});
            }
            """;

        var helpers = new List<string>(s_helpers) { SubstituteHelper.Source };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<DbAssertDynamodbModel> ───────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(DbAssertDynamodbModel model)
    {
        yield return new ResourceRequirement(
            Family: "dynamodb",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>AWSSDK.DynamoDBv2</c> assembly so the Roslyn compiler can resolve
    /// <c>AmazonDynamoDBClient</c>, <c>AttributeValue</c>, and related types in the emitted
    /// helper class. Already loaded in the Default ALC (the provider project references it
    /// directly) and must never be loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Amazon.DynamoDBv2.AmazonDynamoDBClient).Assembly;

            // AWSSDK.Core — the emitted helper names Amazon.Runtime.BasicAWSCredentials
            // and ClientConfig-inherited members (ServiceURL/MaxErrorRetry/Timeout)
            // directly, so the contract requires declaring it even though it also
            // arrives transitively on the CLI's trusted-platform-assembly list
            // (the azureservicebus → Azure.Core precedent).
            yield return typeof(Amazon.Runtime.BasicAWSCredentials).Assembly;
        }
    }

    // ── IStepDiffRenderer ──────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is the
    /// <c>{"field":…,"expected":…,"actual":…}</c> attribute-mismatch shape this provider can
    /// render as an expected-vs-observed diff (mirrors
    /// <c>DbAssertMongodbProvider.CanRender</c>'s field-diff branch).
    /// </summary>
    public bool CanRender(JsonElement observation) =>
        TryReadFieldDiff(observation, out _, out _, out _);

    /// <inheritdoc />
    public string? RenderDiff(JsonElement observation)
    {
        if (TryReadFieldDiff(observation, out var field, out var expected, out var actual))
        {
            return RenderFieldTable(field, expected, actual);
        }

        return null;
    }

    // ── IStepDiffRenderer helpers ──────────────────────────────────────────────

    private static bool TryReadFieldDiff(
        JsonElement observation,
        out string field,
        out string expected,
        out string actual)
    {
        field = string.Empty;
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("field", out var fieldEl)
            || fieldEl.ValueKind != JsonValueKind.String
            || !observation.TryGetProperty("expected", out var expectedEl)
            || !observation.TryGetProperty("actual", out var actualEl))
        {
            return false;
        }

        field = fieldEl.GetString() ?? string.Empty;
        expected = ScalarText(expectedEl);
        actual = ScalarText(actualEl);
        return true;
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "null",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static string RenderFieldTable(string field, string expected, string actual)
    {
        var headers = new[] { "field", "expected", "actual" };
        var values = new[] { field, expected, actual };
        return RenderTable(headers, values);
    }

    private static string RenderTable(string[] headers, string[] values)
    {
        var widths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            widths[i] = Math.Max(headers[i].Length, values[i].Length);
        }

        var sb = new System.Text.StringBuilder();

        AppendRow(sb, headers, widths);

        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0)
                sb.Append('┼');
            sb.Append(new string('─', widths[i] + 2));
        }
        sb.Append('\n');

        AppendRow(sb, values, widths);

        return sb.ToString();
    }

    private static void AppendRow(System.Text.StringBuilder sb, string[] cells, int[] widths)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                sb.Append('│');
            sb.Append(' ');
            sb.Append(cells[i].PadRight(widths[i]));
            sb.Append(' ');
        }
        sb.Append('\n');
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string BuildStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
            return "new string[] { }";

        var sb = new System.Text.StringBuilder("new string[] { ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(JsonSerializer.Serialize(values[i]));
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static string BuildResolvedArrayLiteral(string[] expressions)
    {
        if (expressions.Length == 0)
            return "new string[] { }";

        var sb = new System.Text.StringBuilder("new string[] { ");
        for (int i = 0; i < expressions.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(expressions[i]);
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }
}
