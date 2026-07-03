// Platform.Steps.DbAssert.Mongodb — db-assert.mongodb step provider (DSL §5, §13.10).
//
// Executes a JSON-filter query against a declared MongoDB dependency and asserts
// on the matched-document count and/or individual field values.
//
// Injection safety (BSON operator injection):
//   • The filter field accepts a JSON template with {placeholder} tokens.
//   • At runtime inside ExecuteAsync the helper calls ResolveFilter, which uses
//     System.Text.Json.JsonSerializer.Serialize(value) to JSON-escape each resolved
//     placeholder value before splicing it into the filter JSON.  A value like
//     {"$gt":""} becomes {\"$gt\":\"\"} inside the string — a literal string value,
//     not a nested BSON object — blocking operator injection.  Placeholders OUTSIDE
//     a JSON string literal are not supported (authors must quote them).
//   • Field expected values support arbitrary substitution via Substitute_Helpers.Resolve.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     contains the full provider-id-prefixed static class definition; StatementBlock
//     is a C# 11 $$"""…""" block; 'using var' is illegal.
using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.DbAssert.Mongodb;

/// <summary>
/// Core provider for the <c>db-assert.mongodb</c> step kind (DSL §5).
/// Executes a JSON-filter query against a declared MongoDB dependency and
/// asserts on the matched-document count and/or individual field values.
/// </summary>
[StepProvider]
public sealed class DbAssertMongodbProvider
    : IStepProvider,
      IStepBinder<DbAssertMongodbModel>,
      IStepValidator<DbAssertMongodbModel>,
      IStepCompiler<DbAssertMongodbModel>,
      IResourceContributor<DbAssertMongodbModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("db-assert", "mongodb");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<DbAssertMongodbModel> ─────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>db-assert.mongodb</c>
    /// provider's own fields.
    /// </summary>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "collection", "filter", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the mongodb dependency to query, as declared under environment.dependencies.",
              "type": "string"
            },
            "collection": {
              "description": "Name of the MongoDB collection to query.",
              "type": "string"
            },
            "filter": {
              "description": "JSON filter document.  May contain {placeholder} tokens resolved at runtime.",
              "type": "string"
            },
            "expect": {
              "description": "Assertion block declaring the expected query outcome.  At least one of count or document must be specified.",
              "type": "object",
              "properties": {
                "count": {
                  "description": "Expected number of documents matched by the filter.",
                  "type": "integer"
                },
                "document": {
                  "description": "Map of flat (top-level) field name to expected string value, asserted against the first matched document. Dot-notation paths are not supported in v1.",
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
    public DbAssertMongodbModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new DbAssertMongodbModel(
                Target: string.Empty,
                Collection: string.Empty,
                Filter: string.Empty,
                Expect: new MongoExpectation(Count: null, Document: null));
        }

        var target = GetScalar(mapping, "target");
        var collection = GetScalar(mapping, "collection");
        var filter = GetScalar(mapping, "filter");

        long? count = null;
        IReadOnlyDictionary<string, string>? document = null;

        if (mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            && expectNode is YamlMappingNode expectMap)
        {
            if (expectMap.Children.TryGetValue(new YamlScalarNode("count"), out var countNode)
                && countNode is YamlScalarNode countScalar
                && long.TryParse(countScalar.Value, out var parsedCount))
            {
                count = parsedCount;
            }

            if (expectMap.Children.TryGetValue(new YamlScalarNode("document"), out var docNode)
                && docNode is YamlMappingNode docMap)
            {
                var docDict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in docMap.Children)
                {
                    if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                        docDict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
                }
                document = docDict;
            }
        }

        return new DbAssertMongodbModel(
            Target: target,
            Collection: collection,
            Filter: filter,
            Expect: new MongoExpectation(Count: count, Document: document));
    }

    // ── IStepValidator<DbAssertMongodbModel> ──────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(DbAssertMongodbModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("db-assert.mongodb: 'target' must not be empty.");

        // (b) collection must not be empty.
        if (string.IsNullOrWhiteSpace(model.Collection))
            errors.Add("db-assert.mongodb: 'collection' must not be empty.");

        // (c) filter must not be empty.
        if (string.IsNullOrWhiteSpace(model.Filter))
            errors.Add("db-assert.mongodb: 'filter' must not be empty.");

        // (c2) filter must not use server-side JavaScript operators.
        // Try-parse as BsonDocument; if it contains {placeholder} tokens the parse
        // will fail — skip this check in that case (ExecuteAsync validates at runtime).
        if (!string.IsNullOrWhiteSpace(model.Filter))
        {
            try
            {
                var filterDoc = BsonDocument.Parse(model.Filter);
                if (ContainsDeniedOperator(filterDoc, out var foundKey))
                {
                    errors.Add(
                        $"db-assert.mongodb: Filter uses server-side JavaScript operator " +
                        $"'{foundKey}' which is not permitted; use structural query operators instead.");
                }
            }
            catch (Exception)
            {
                // Filter contains {placeholder} tokens or other non-parseable content
                // (BsonDocument.Parse throws FormatException or other Exception types).
                // The operator check is enforced at runtime inside ExecuteAsync.
            }
        }

        // (d) expect must declare at least one assertion.
        if (model.Expect.Count is null
            && (model.Expect.Document is null || model.Expect.Document.Count == 0))
        {
            errors.Add(
                "db-assert.mongodb: 'expect' must specify count and/or document.");
        }

        // (e) document field names must be top-level keys; dot-notation paths are not
        //     supported in v1. A dotted key silently produces an incorrect Fail because
        //     firstDoc.Contains("a.b") is always false even when "a.b" is a nested path.
        //     Reject it here with a loud error rather than allowing a mis-verdict.
        if (model.Expect.Document is not null)
        {
            foreach (var fieldName in model.Expect.Document.Keys)
            {
                if (fieldName.Contains('.', StringComparison.Ordinal))
                    errors.Add(
                        $"db-assert.mongodb: expect.document field name '{fieldName}' contains " +
                        "'.'. Dot-notation paths are not supported in v1. " +
                        "Use a flat (top-level) field name instead.");
            }
        }

        // (f) dependency reconciliation: target must name a declared mongodb dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"db-assert.mongodb: 'target' '{model.Target}' is not a " +
                    "mongodb dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "mongodb", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"db-assert.mongodb: 'target' '{model.Target}' is not a " +
                    "mongodb dependency declared in environment.dependencies.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "System.Threading.Tasks",
            "MongoDB.Driver",
            "MongoDB.Bson",
            "Platform.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>DbAssertMongodb_</c> to prevent collisions
    /// when multiple providers contribute helpers to the same Roslyn submission.
    /// All types are fully-qualified so the helper compiles independently of
    /// the spliced <c>using</c> ordering.  <c>using var</c> is absent — explicit
    /// <c>.Dispose()</c> calls in <c>finally</c> blocks are used throughout.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same
    /// provider within a suite (§13.3.1 dedup rule); it contains no
    /// per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class DbAssertMongodb_Helpers\n" +
        "{\n" +
        "    private static readonly System.Text.RegularExpressions.Regex _placeholderRegex =\n" +
        "        new System.Text.RegularExpressions.Regex(\"{([A-Za-z_][A-Za-z0-9_]*)}\");\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Resolves {placeholder} tokens in a JSON filter template.\n" +
        "    /// Each resolved value is JSON-escaped before splicing to prevent\n" +
        "    /// BSON operator injection (e.g. a value containing {\"$gt\":\"\"} becomes\n" +
        "    /// a JSON-escaped string literal inside the filter, not a nested object).\n" +
        "    /// Placeholders not found in vars are kept as literal text.\n" +
        "    /// </summary>\n" +
        "    public static string ResolveFilter(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string filterTemplate)\n" +
        "    {\n" +
        "        return _placeholderRegex.Replace(filterTemplate, m =>\n" +
        "        {\n" +
        "            var name = m.Groups[1].Value;\n" +
        "            if (!vars.TryGetValue(name, out var val) || val is null)\n" +
        "                return m.Value;\n" +
        "            var strVal = val is string sv ? sv : System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;\n" +
        "            // JSON-escape the value (Serialize produces \"...\"); strip the outer quotes.\n" +
        "            // This prevents a value like {\"$gt\":\"\"} from injecting a BSON operator.\n" +
        "            var serialised = System.Text.Json.JsonSerializer.Serialize(strVal);\n" +
        "            return serialised.Length >= 2\n" +
        "                ? serialised.Substring(1, serialised.Length - 2)\n" +
        "                : strVal;\n" +
        "        });\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Executes a JSON-filter query via MongoDB.Driver, evaluates the count\n" +
        "    /// and/or document-field expectations, and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing connection string = EnvironmentError (§12.1).\n" +
        "    /// Count or field mismatch = Fail.\n" +
        "    /// Successful assertion = Pass.\n" +
        "    /// </summary>\n" +
        "    public static async System.Threading.Tasks.Task ExecuteAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string collection,\n" +
        "        string filterTemplate,\n" +
        "        long? expectedCount,\n" +
        "        string[] expectFields,\n" +
        "        string[] expectValues)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        // Read the connection string staged by the orchestrator.\n" +
        "        // A null or empty string means the dependency was not discovered = EnvironmentError.\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"connection string not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        // Resolve {placeholder} tokens in the filter template (JSON-escape injection guard).\n" +
        "        string resolvedFilter;\n" +
        "        try\n" +
        "        {\n" +
        "            resolvedFilter = ResolveFilter(vars, filterTemplate);\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, \"filter resolution error: \" + ex.Message)) + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        MongoDB.Driver.MongoClient? client = null;\n" +
        "        try\n" +
        "        {\n" +
        "            client = new MongoDB.Driver.MongoClient(connStr);\n" +
        "            var dbName = new MongoDB.Driver.MongoUrl(connStr).DatabaseName;\n" +
        "            if (string.IsNullOrEmpty(dbName))\n" +
        "                throw new System.InvalidOperationException(\"Could not determine database name from connection string '\" + connKey + \"'.\");\n" +
        "            var db = client.GetDatabase(dbName);\n" +
        "            var coll = db.GetCollection<MongoDB.Bson.BsonDocument>(collection);\n" +
        "            // Parse the resolved filter JSON into a BsonDocument.\n" +
        "            MongoDB.Bson.BsonDocument filterDoc;\n" +
        "            try\n" +
        "            {\n" +
        "                filterDoc = MongoDB.Bson.BsonDocument.Parse(resolvedFilter);\n" +
        "            }\n" +
        "            catch (System.Exception ex)\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, \"filter parse error: \" + ex.Message)) + \"}\";\n" +
        "                sw.Stop();\n" +
        "                client?.Dispose();\n" +
        "                client = null;\n" +
        "                vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);\n" +
        "                return;\n" +
        "            }\n" +
        "            // Runtime guard: denied operators introduced by placeholder substitution → Fail (§11).\n" +
        "            // A filter template can produce an illegal operator after substitution (e.g. a\n" +
        "            // variable value containing '$where').  Catch it here before sending to MongoDB.\n" +
        "            string __deniedOp;\n" +
        "            if (ContainsDeniedOperatorRuntime(filterDoc, out __deniedOp))\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"filter uses denied operator '\" + __deniedOp + \"' after placeholder substitution\") + \"}\";\n" +
        "                sw.Stop();\n" +
        "                client?.Dispose();\n" +
        "                client = null;\n" +
        "                vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);\n" +
        "                return;\n" +
        "            }\n" +
        "            // Two-query approach: CountDocumentsAsync returns the exact count with no document cap,\n" +
        "            // then Limit(1) fetches the first document only when field expectations are declared.\n" +
        "            // Using Limit(1000).ToListAsync() would silently cap actualCount at 1000 (B1 blocker).\n" +
        "            var actualCount = await coll.CountDocumentsAsync(filterDoc).ConfigureAwait(false);\n" +
        "            MongoDB.Bson.BsonDocument? firstDoc = expectFields.Length > 0\n" +
        "                ? await coll.Find(filterDoc).Limit(1).FirstOrDefaultAsync(default(System.Threading.CancellationToken)).ConfigureAwait(false)\n" +
        "                : null;\n" +
        "            string? failObservation = null;\n" +
        "            // Evaluate document-field expectations against the first matched document.\n" +
        "            if (expectFields.Length > 0)\n" +
        "            {\n" +
        "                if (firstDoc is not null)\n" +
        "                {\n" +
        "                    for (int i = 0; i < expectFields.Length && failObservation is null; i++)\n" +
        "                    {\n" +
        "                        var fieldPath = expectFields[i];\n" +
        "                        var expectedVal = expectValues[i];\n" +
        "                        string actualVal;\n" +
        "                        if (firstDoc.Contains(fieldPath))\n" +
        "                        {\n" +
        "                            var bsonVal = firstDoc[fieldPath];\n" +
        "                            actualVal = bsonVal.IsBsonNull\n" +
        "                                ? \"null\"\n" +
        "                                : (bsonVal.BsonType == MongoDB.Bson.BsonType.String\n" +
        "                                    ? bsonVal.AsString\n" +
        "                                    : bsonVal.ToString() ?? \"null\");\n" +
        "                        }\n" +
        "                        else\n" +
        "                        {\n" +
        "                            actualVal = \"null\";\n" +
        "                        }\n" +
        "                        if (!string.Equals(actualVal, expectedVal, System.StringComparison.Ordinal))\n" +
        "                        {\n" +
        "                            failObservation =\n" +
        "                                \"{\\\"field\\\":\" + System.Text.Json.JsonSerializer.Serialize(fieldPath) +\n" +
        "                                \",\\\"expected\\\":\" + System.Text.Json.JsonSerializer.Serialize(expectedVal) +\n" +
        "                                \",\\\"actual\\\":\" + System.Text.Json.JsonSerializer.Serialize(actualVal) + \"}\";\n" +
        "                        }\n" +
        "                    }\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        "                    // No document matched at all; report the first field expectation as a mismatch.\n" +
        "                    failObservation =\n" +
        "                        \"{\\\"field\\\":\" + System.Text.Json.JsonSerializer.Serialize(expectFields[0]) +\n" +
        "                        \",\\\"expected\\\":\" + System.Text.Json.JsonSerializer.Serialize(expectValues[0]) +\n" +
        "                        \",\\\"actual\\\":\\\"null\\\"}\";\n" +
        "                }\n" +
        "            }\n" +
        "            // Evaluate count expectation (only if no field mismatch already).\n" +
        "            if (failObservation is null && expectedCount.HasValue && actualCount != expectedCount.Value)\n" +
        "            {\n" +
        "                failObservation =\n" +
        "                    \"{\\\"count\\\":{\\\"expected\\\":\" +\n" +
        "                    expectedCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +\n" +
        "                    \",\\\"actual\\\":\" +\n" +
        "                    actualCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}}\";\n" +
        "            }\n" +
        "            if (failObservation is not null)\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = failObservation;\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "                observation = \"{\\\"count\\\":\" + actualCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "        }\n" +
        "        catch (MongoDB.Driver.MongoException ex)\n" +
        "        {\n" +
        "            // MongoDB-specific exception: network failure, auth error, etc. = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection or protocol failure = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            client?.Dispose();  // explicit Dispose() in finally (§13.3.1). MongoDB.Driver 3.x IMongoClient extends IDisposable.\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Redacts credential material from an exception message (§17 — no secrets in observations).\n" +
        "    /// Removes: (1) the full connection string if it appears literally;\n" +
        "    ///          (2) MongoDB userinfo (user:pwd@ segment in mongodb:// URI);\n" +
        "    ///          (3) ADO-style Password=/Pwd= key-value pairs.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactCredentials(string connStr, string message)\n" +
        "    {\n" +
        "        if (!string.IsNullOrEmpty(connStr))\n" +
        "            message = message.Replace(connStr, \"***\", System.StringComparison.Ordinal);\n" +
        "        // MongoDB URI: redact userinfo (mongodb://user:pwd@host → mongodb://***@host).\n" +
        "        message = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            message,\n" +
        "            \"mongodb(?:\\\\+srv)?://[^@/]+@\",\n" +
        "            \"mongodb://***@\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        // ADO-style: redact Password= / Pwd= values.\n" +
        "        message = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            message,\n" +
        "            \"(?:Password|Pwd)\\\\s*=\\\\s*[^;]+\",\n" +
        "            \"Password=***\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return message;\n" +
        "    }\n" +
        "\n" +
        "    // NOTE: Compile-time copy lives in ContainsDeniedOperator() below — keep both in sync.\n" +
        "    /// <summary>\n" +
        "    /// Recursively walks a BsonDocument and returns true if any key at any nesting level\n" +
        "    /// is a server-side JavaScript operator ($where, $function, $accumulator).\n" +
        "    /// Called at runtime after placeholder substitution, complementing the compile-time\n" +
        "    /// Validate-phase check (which catches static operators but cannot check resolved values).\n" +
        "    /// </summary>\n" +
        "    private static bool ContainsDeniedOperatorRuntime(MongoDB.Bson.BsonDocument doc, out string foundKey)\n" +
        "    {\n" +
        "        foreach (var element in doc.Elements)\n" +
        "        {\n" +
        "            if (string.Equals(element.Name, \"$where\", System.StringComparison.Ordinal)\n" +
        "                || string.Equals(element.Name, \"$function\", System.StringComparison.Ordinal)\n" +
        "                || string.Equals(element.Name, \"$accumulator\", System.StringComparison.Ordinal))\n" +
        "            {\n" +
        "                foundKey = element.Name;\n" +
        "                return true;\n" +
        "            }\n" +
        "            if (element.Value.IsBsonDocument\n" +
        "                && ContainsDeniedOperatorRuntime(element.Value.AsBsonDocument, out foundKey))\n" +
        "            {\n" +
        "                return true;\n" +
        "            }\n" +
        "            if (element.Value.IsBsonArray\n" +
        "                && ContainsDeniedOperatorArrayRuntime(element.Value.AsBsonArray, out foundKey))\n" +
        "            {\n" +
        "                return true;\n" +
        "            }\n" +
        "        }\n" +
        "        foundKey = string.Empty;\n" +
        "        return false;\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Recursively walks a BsonArray, delegating document elements to\n" +
        "    /// ContainsDeniedOperatorRuntime and nested arrays back to this method.\n" +
        "    /// </summary>\n" +
        "    private static bool ContainsDeniedOperatorArrayRuntime(MongoDB.Bson.BsonArray array, out string foundKey)\n" +
        "    {\n" +
        "        foreach (var item in array)\n" +
        "        {\n" +
        "            if (item.IsBsonDocument\n" +
        "                && ContainsDeniedOperatorRuntime(item.AsBsonDocument, out foundKey))\n" +
        "            {\n" +
        "                return true;\n" +
        "            }\n" +
        "            if (item.IsBsonArray\n" +
        "                && ContainsDeniedOperatorArrayRuntime(item.AsBsonArray, out foundKey))\n" +
        "            {\n" +
        "                return true;\n" +
        "            }\n" +
        "        }\n" +
        "        foundKey = string.Empty;\n" +
        "        return false;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<DbAssertMongodbModel> ───────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(DbAssertMongodbModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Expand document-field expectations into parallel arrays.
        string[] expectFields;
        string[] expectValues;
        if (model.Expect.Document is { Count: > 0 } document)
        {
            expectFields = document.Keys.ToArray();
            expectValues = document.Values.ToArray();
        }
        else
        {
            expectFields = Array.Empty<string>();
            expectValues = Array.Empty<string>();
        }

        // Emit expectedCount as a bare long literal or 'null'.
        // Appending 'L' ensures C# treats it as a long, not an int.
        var countLiteral = model.Expect.Count is long c
            ? c.ToString(CultureInfo.InvariantCulture) + "L"
            : "null";

        // Filter template is passed as a raw JSON-escaped string literal.
        // ResolveFilter is called INSIDE the helper at runtime — never at emit time.
        // This keeps the filter template intact for inspection and matches the pattern
        // where identifiers are resolved inside helpers (H1 blast-radius containment).
        var filterLiteral = JsonSerializer.Serialize(model.Filter);

        // Document field expected VALUES are wrapped in Substitute_Helpers.Resolve so that
        // {placeholder} tokens in expected values resolve at runtime from Vars.
        var resolvedExpectValues = new string[expectValues.Length];
        for (int i = 0; i < expectValues.Length; i++)
        {
            resolvedExpectValues[i] =
                $"Substitute_Helpers.Resolve(Vars, {JsonSerializer.Serialize(expectValues[i])})";
        }

        var expectFieldsLiteral = BuildStringArrayLiteral(expectFields);
        var expectValuesLiteral = BuildResolvedArrayLiteral(resolvedExpectValues);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        var block = $$"""
            {
                await DbAssertMongodb_Helpers.ExecuteAsync(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{JsonSerializer.Serialize(model.Collection)}},
                    {{filterLiteral}},
                    {{countLiteral}},
                    {{expectFieldsLiteral}},
                    {{expectValuesLiteral}});
            }
            """;

        // Include SubstituteHelper.Source in RequiredHelpers (needed for document field
        // value substitution via Substitute_Helpers.Resolve in the StatementBlock).
        // CsxAssembler deduplicates by class name so it is included at most once.
        var helpers = new List<string>(s_helpers) { SubstituteHelper.Source };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<DbAssertMongodbModel> ────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(DbAssertMongodbModel model)
    {
        yield return new ResourceRequirement(
            Family: "mongodb",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>MongoDB.Driver</c> and <c>MongoDB.Bson</c> assemblies so the
    /// Roslyn compiler can resolve <c>MongoClient</c>, <c>MongoUrl</c>,
    /// <c>BsonDocument</c> and related types in the emitted helper class.
    /// Both assemblies are already loaded in the Default ALC (the provider project
    /// references MongoDB.Driver directly) and must never be loaded into the
    /// collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(MongoDB.Driver.MongoClient).Assembly;   // MongoDB.Driver.dll
            yield return typeof(MongoDB.Bson.BsonDocument).Assembly;    // MongoDB.Bson.dll
        }
    }

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is one of the
    /// <c>db-assert.mongodb</c> Fail-observation shapes that this provider can render
    /// as an expected-vs-observed diff.
    /// </summary>
    /// <remarks>
    /// Recognised shapes (emitted by <c>DbAssertMongodb_Helpers</c> on a Fail verdict):
    /// <list type="bullet">
    ///   <item><description><c>{"field":…,"expected":…,"actual":…}</c> — a document-field mismatch.</description></item>
    ///   <item><description><c>{"count":{"expected":…,"actual":…}}</c> — a count mismatch.</description></item>
    /// </list>
    /// </remarks>
    public bool CanRender(JsonElement observation) =>
        TryReadFieldDiff(observation, out _, out _, out _)
        || TryReadCountDiff(observation, out _, out _);

    /// <inheritdoc />
    public string? RenderDiff(JsonElement observation)
    {
        if (TryReadFieldDiff(observation, out var field, out var expected, out var actual))
        {
            return RenderFieldTable(field, expected, actual);
        }

        if (TryReadCountDiff(observation, out var expectedCount, out var actualCount))
        {
            return RenderCountTable(expectedCount, actualCount);
        }

        return null;
    }

    // ── IStepDiffRenderer helpers ─────────────────────────────────────────────

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

    private static bool TryReadCountDiff(
        JsonElement observation,
        out string expected,
        out string actual)
    {
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("count", out var countEl)
            || countEl.ValueKind != JsonValueKind.Object
            || !countEl.TryGetProperty("expected", out var expectedEl)
            || !countEl.TryGetProperty("actual", out var actualEl))
        {
            return false;
        }

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

    private static string RenderCountTable(string expected, string actual)
    {
        var headers = new[] { "count", "expected", "actual" };
        var values = new[] { "(docs)", expected, actual };
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

    // ── Private helpers ───────────────────────────────────────────────────────

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

    // NOTE: Runtime copy lives inside the emitted helper string above — keep both in sync.
    /// <summary>
    /// Recursively walks a <see cref="BsonDocument"/> and returns <see langword="true"/>
    /// if any key at any nesting level is <c>$where</c>, <c>$function</c>, or
    /// <c>$accumulator</c> (server-side JavaScript operators that bypass the BSON
    /// injection guard).  <c>$expr</c> is intentionally NOT blocked.
    /// </summary>
    private static bool ContainsDeniedOperator(BsonDocument doc, out string foundKey)
    {
        foreach (var element in doc.Elements)
        {
            if (string.Equals(element.Name, "$where", StringComparison.Ordinal)
                || string.Equals(element.Name, "$function", StringComparison.Ordinal)
                || string.Equals(element.Name, "$accumulator", StringComparison.Ordinal))
            {
                foundKey = element.Name;
                return true;
            }

            if (element.Value.IsBsonDocument
                && ContainsDeniedOperator(element.Value.AsBsonDocument, out foundKey))
            {
                return true;
            }

            if (element.Value.IsBsonArray
                && ContainsDeniedOperatorArray(element.Value.AsBsonArray, out foundKey))
            {
                return true;
            }
        }

        foundKey = string.Empty;
        return false;
    }

    /// <summary>
    /// Recursively walks a <see cref="BsonArray"/>, delegating document elements to
    /// <see cref="ContainsDeniedOperator"/> and nested arrays back to this method.
    /// </summary>
    private static bool ContainsDeniedOperatorArray(BsonArray array, out string foundKey)
    {
        foreach (var item in array)
        {
            if (item.IsBsonDocument
                && ContainsDeniedOperator(item.AsBsonDocument, out foundKey))
            {
                return true;
            }

            if (item.IsBsonArray
                && ContainsDeniedOperatorArray(item.AsBsonArray, out foundKey))
            {
                return true;
            }
        }

        foundKey = string.Empty;
        return false;
    }
}
