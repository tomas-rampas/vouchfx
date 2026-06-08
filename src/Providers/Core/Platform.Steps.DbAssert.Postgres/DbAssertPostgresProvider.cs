// Platform.Steps.DbAssert.Postgres — db-assert.postgres step provider (DSL §5, §13.10).
//
// F-01 implementation: IStepProvider + IStepBinder<T> + IStepValidator<T>.
// F-02 adds: IStepCompiler<T> + IResourceContributor<T> + ICompileReferenceContributor.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, query,
//     parameters, expect).  The type const discriminator is injected by the
//     SchemaComposer from Kind — never from the fragment text.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     contains the full provider-id-prefixed static class definition; StatementBlock
//     is a C# 11 $$"""…""" block; 'using var' is illegal.
using System.Globalization;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.DbAssert.Postgres;

/// <summary>
/// Core provider for the <c>db-assert.postgres</c> step kind (DSL §5).
/// Executes a parameterised SQL query against a declared Postgres dependency
/// and asserts on the row count and/or individual column values.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.
/// The engine's <c>SchemaComposer</c> assembles the unified schema by injecting
/// a <c>const</c>-keyed <c>if</c>/<c>then</c> discriminator derived from
/// <see cref="Kind"/> — the fragment text never repeats that discriminator (§13.6).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a <see cref="CsxFragment"/> whose emitted
/// CSX executes a parameterised Npgsql query, evaluates row-count and/or column-value
/// expectations, and writes a typed <see cref="StepOutcome"/> into <c>Vars</c> for
/// the runner to read after execution (§13.3.1).  Placeholder substitution of query
/// and parameter values is deferred to S04-B-03; raw values are emitted here.
/// </para>
/// </remarks>
[StepProvider]
public sealed class DbAssertPostgresProvider
    : IStepProvider,
      IStepBinder<DbAssertPostgresModel>,
      IStepValidator<DbAssertPostgresModel>,
      IStepCompiler<DbAssertPostgresModel>,
      IResourceContributor<DbAssertPostgresModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("db-assert", "postgres");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<DbAssertPostgresModel> ────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>db-assert.postgres</c>
    /// provider's own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it
    /// as an <c>if</c>/<c>then</c> clause (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "query", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the postgres dependency to query, as declared under environment.dependencies.",
              "type": "string"
            },
            "query": {
              "description": "The SQL query to execute.  May be a multi-line literal.",
              "type": "string"
            },
            "parameters": {
              "description": "Optional map of SQL parameter names (without leading '@') to their string values.",
              "type": "object",
              "additionalProperties": { "type": "string" }
            },
            "expect": {
              "description": "Assertion block declaring the expected query outcome.  At least one of rowCount or row must be specified.",
              "type": "object",
              "properties": {
                "rowCount": {
                  "description": "Expected number of rows returned by the query.",
                  "type": "integer"
                },
                "row": {
                  "description": "Map of column name to expected string value, asserted against the first row.",
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
    public DbAssertPostgresModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new DbAssertPostgresModel(
                Target: string.Empty,
                Query: string.Empty,
                Parameters: null,
                Expect: new PostgresExpectation(RowCount: null, Row: null));
        }

        var target = GetScalar(mapping, "target");
        var query = GetScalar(mapping, "query");

        IReadOnlyDictionary<string, string>? parameters = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("parameters"), out var paramsNode)
            && paramsNode is YamlMappingNode paramsMap)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in paramsMap.Children)
            {
                if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                    dict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
            }
            parameters = dict;
        }

        int? rowCount = null;
        IReadOnlyDictionary<string, string>? row = null;

        if (mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            && expectNode is YamlMappingNode expectMap)
        {
            if (expectMap.Children.TryGetValue(new YamlScalarNode("rowCount"), out var rowCountNode)
                && rowCountNode is YamlScalarNode rowCountScalar
                && int.TryParse(rowCountScalar.Value, out var parsedRowCount))
            {
                rowCount = parsedRowCount;
            }

            if (expectMap.Children.TryGetValue(new YamlScalarNode("row"), out var rowNode)
                && rowNode is YamlMappingNode rowMap)
            {
                var rowDict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in rowMap.Children)
                {
                    if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                        rowDict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
                }
                row = rowDict;
            }
        }

        return new DbAssertPostgresModel(
            Target: target,
            Query: query,
            Parameters: parameters,
            Expect: new PostgresExpectation(RowCount: rowCount, Row: row));
    }

    // ── IStepValidator<DbAssertPostgresModel> ─────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(DbAssertPostgresModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("db-assert.postgres: 'target' must not be empty.");

        // (b) query must not be empty.
        if (string.IsNullOrWhiteSpace(model.Query))
            errors.Add("db-assert.postgres: 'query' must not be empty.");

        // (c) expect must declare at least one assertion.
        if (model.Expect.RowCount is null
            && (model.Expect.Row is null || model.Expect.Row.Count == 0))
        {
            errors.Add(
                "db-assert.postgres: 'expect' must specify rowCount and/or row.");
        }

        // (d) dependency reconciliation: target must name a declared postgres dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"db-assert.postgres: 'target' '{model.Target}' is not a " +
                    "postgres dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"db-assert.postgres: 'target' '{model.Target}' is not a " +
                    "postgres dependency declared in environment.dependencies.");
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
            "Npgsql",
            "Platform.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>DbAssertPostgres_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
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
        "static class DbAssertPostgres_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Executes a parameterised SQL query via Npgsql, evaluates the\n" +
        "    /// row-count and/or column-value expectations, and writes a typed\n" +
        "    /// StepOutcome into Vars.\n" +
        "    /// Missing connection string = EnvironmentError (§12.1).\n" +
        "    /// Row-count or column mismatch = Fail.\n" +
        "    /// Successful assertion = Pass.\n" +
        "    /// </summary>\n" +
        "    public static async System.Threading.Tasks.Task ExecuteAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string query,\n" +
        "        string[] paramNames,\n" +
        "        string[] paramValues,\n" +
        "        int? expectedRowCount,\n" +
        "        string[] expectColumns,\n" +
        "        string[] expectValues)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        // Read the connection string staged by the orchestrator (VarKeys.Connection pattern).\n" +
        "        // A null or empty string means the dependency was not discovered → EnvironmentError (§12.1).\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\\\"connection string not found for key '\" + connKey + \"'\\\"}\" );\n" +
        "            return;\n" +
        "        }\n" +
        "        var conn = new Npgsql.NpgsqlConnection(connStr);\n" +
        "        try\n" +
        "        {\n" +
        "            await conn.OpenAsync().ConfigureAwait(false);\n" +
        "            var cmd = conn.CreateCommand();\n" +
        "            try\n" +
        "            {\n" +
        "                cmd.CommandText = query;\n" +
        "                for (int i = 0; i < paramNames.Length; i++)\n" +
        "                {\n" +
        "                    cmd.Parameters.AddWithValue(paramNames[i], (object)paramValues[i]);\n" +
        "                }\n" +
        "                var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);\n" +
        "                try\n" +
        "                {\n" +
        "                    int actualRowCount = 0;\n" +
        "                    string? failObservation = null;\n" +
        "                    bool firstRow = true;\n" +
        "                    while (await reader.ReadAsync().ConfigureAwait(false))\n" +
        "                    {\n" +
        "                        actualRowCount++;\n" +
        "                        // Evaluate column expectations against the first row only.\n" +
        "                        if (firstRow && expectColumns.Length > 0 && failObservation is null)\n" +
        "                        {\n" +
        "                            for (int ci = 0; ci < expectColumns.Length; ci++)\n" +
        "                            {\n" +
        "                                var colName = expectColumns[ci];\n" +
        "                                var expectedVal = expectValues[ci];\n" +
        "                                object? rawVal = null;\n" +
        "                                try\n" +
        "                                {\n" +
        "                                    rawVal = reader[colName];\n" +
        "                                }\n" +
        "                                catch (System.Exception)\n" +
        "                                {\n" +
        "                                    rawVal = null;\n" +
        "                                }\n" +
        "                                var actualVal = rawVal is System.DBNull || rawVal is null\n" +
        "                                    ? \"null\"\n" +
        "                                    : rawVal.ToString() ?? \"null\";\n" +
        "                                if (!string.Equals(actualVal, expectedVal, System.StringComparison.Ordinal))\n" +
        "                                {\n" +
        "                                    failObservation =\n" +
        "                                        \"{\\\"column\\\":\" + System.Text.Json.JsonSerializer.Serialize(colName) +\n" +
        "                                        \",\\\"expected\\\":\" + System.Text.Json.JsonSerializer.Serialize(expectedVal) +\n" +
        "                                        \",\\\"actual\\\":\" + System.Text.Json.JsonSerializer.Serialize(actualVal) + \"}\";\n" +
        "                                    break;\n" +
        "                                }\n" +
        "                            }\n" +
        "                        }\n" +
        "                        firstRow = false;\n" +
        "                    }\n" +
        "                    // Evaluate row-count expectation.\n" +
        "                    if (failObservation is null && expectedRowCount.HasValue && actualRowCount != expectedRowCount.Value)\n" +
        "                    {\n" +
        "                        failObservation =\n" +
        "                            \"{\\\"rowCount\\\":{\\\"expected\\\":\" + expectedRowCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +\n" +
        "                            \",\\\"actual\\\":\" + actualRowCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}}\";\n" +
        "                    }\n" +
        "                    if (failObservation is not null)\n" +
        "                    {\n" +
        "                        verdict = Platform.Engine.Abstractions.Verdict.Fail;\n" +
        "                        observation = failObservation;\n" +
        "                    }\n" +
        "                    else\n" +
        "                    {\n" +
        "                        verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "                        observation = \"{\\\"rowCount\\\":\" + actualRowCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "                    }\n" +
        "                }\n" +
        "                finally\n" +
        "                {\n" +
        "                    reader.Dispose();  // explicit Dispose() in finally (§13.3.1).\n" +
        "                }\n" +
        "            }\n" +
        "            finally\n" +
        "            {\n" +
        "                cmd.Dispose();  // explicit Dispose() in finally (§13.3.1).\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Npgsql.NpgsqlException ex)\n" +
        "        {\n" +
        "            // Npgsql-specific exception: network failure, auth error, etc. = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection or protocol failure = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            conn.Dispose();  // explicit Dispose() in finally (§13.3.1).\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<DbAssertPostgresModel> ──────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution opens an Npgsql connection keyed by
    /// <c>VarKeys.Connection(model.Target)</c>, runs <c>model.Query</c> with the
    /// declared parameters bound as <c>NpgsqlParameter</c> instances (parameterised
    /// — never concatenated into SQL), evaluates the <c>expect.rowCount</c> and/or
    /// <c>expect.row</c> assertions, and writes a typed <see cref="StepOutcome"/>
    /// into <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read
    /// after the script returns.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full <c>static class DbAssertPostgres_Helpers</c> definition; byte-identical across instances.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block; no <c>using var</c>.</item>
    ///   <item>Model values are emitted as <c>JsonSerializer.Serialize</c>-escaped C# string literals.</item>
    ///   <item>The step id is sanitised via <c>CsxFragment.SanitiseId</c> before splicing.</item>
    ///   <item>Placeholder substitution of query / parameters is deferred to S04-B-03.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(DbAssertPostgresModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Expand the parameters map into parallel arrays for the helper signature.
        // Using parallel arrays avoids a Dictionary dependency inside the CSX body.
        string[] paramNames;
        string[] paramValues;
        if (model.Parameters is { Count: > 0 } parameters)
        {
            paramNames = parameters.Keys.ToArray();
            paramValues = parameters.Values.ToArray();
        }
        else
        {
            paramNames = Array.Empty<string>();
            paramValues = Array.Empty<string>();
        }

        // Expand the row-expectation map into parallel arrays for the helper signature.
        string[] expectColumns;
        string[] expectValues;
        if (model.Expect.Row is { Count: > 0 } row)
        {
            expectColumns = row.Keys.ToArray();
            expectValues = row.Values.ToArray();
        }
        else
        {
            expectColumns = Array.Empty<string>();
            expectValues = Array.Empty<string>();
        }

        // Emit expectedRowCount as a bare integer literal or 'null' — not a quoted string.
        // Safe because it is a bounded integer value, not user-controlled text.
        var rowCountLiteral = model.Expect.RowCount is int rc
            ? rc.ToString(CultureInfo.InvariantCulture)
            : "null";

        // Emit parameter/column/value arrays as JSON-encoded individual string literals
        // inside C# array initialisers, following the same discipline as http.rest:
        // each element is wrapped with JsonSerializer.Serialize so embedded quotes,
        // backslashes, and control characters are escaped before splicing into CSX.
        var paramNamesLiteral = BuildStringArrayLiteral(paramNames);
        var paramValuesLiteral = BuildStringArrayLiteral(paramValues);
        var expectColumnsLiteral = BuildStringArrayLiteral(expectColumns);
        var expectValuesLiteral = BuildStringArrayLiteral(expectValues);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        //
        // Outcome key, connection key, and query are emitted via JsonSerializer.Serialize,
        // which wraps each value in double-quotes and escapes any embedded quotes, backslashes,
        // or control characters.  This prevents CSX-literal breakage and removes a string-
        // injection surface.
        var block = $$"""
            {
                await DbAssertPostgres_Helpers.ExecuteAsync(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{JsonSerializer.Serialize(model.Query)}},
                    {{paramNamesLiteral}},
                    {{paramValuesLiteral}},
                    {{rowCountLiteral}},
                    {{expectColumnsLiteral}},
                    {{expectValuesLiteral}});
            }
            """;

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: s_helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<DbAssertPostgresModel> ───────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="ResourceRequirement"/> with
    /// <c>Family="postgres"</c> and <c>Name=model.Target</c>.
    /// The engine maps the <c>Name</c> to the database resource (not the server)
    /// so the connection string is resolved from the most-specific Aspire resource
    /// (§4 WaitFor invariant).
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(DbAssertPostgresModel model)
    {
        yield return new ResourceRequirement(
            Family: "postgres",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>Npgsql</c> assembly so the Roslyn compiler can resolve
    /// <c>NpgsqlConnection</c> and related types in the emitted helper class.
    /// The assembly is already loaded in the Default ALC (the provider project
    /// references it directly) and must never be loaded into the collectible ALC
    /// (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Npgsql.NpgsqlConnection).Assembly;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a C# array-initialiser literal from a string array, with each
    /// element individually JSON-serialised to escape embedded quotes, backslashes,
    /// and control characters before splicing into the CSX StatementBlock.
    /// </summary>
    /// <remarks>
    /// Example: <c>["a", "b\"c"]</c> →
    /// <c>new string[] { "a", "b\"c" }</c>
    /// where the inner quotes are escaped by <see cref="JsonSerializer.Serialize"/>.
    /// </remarks>
    private static string BuildStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
        {
            return "new string[] { }";
        }

        var sb = new System.Text.StringBuilder("new string[] { ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            // JsonSerializer.Serialize produces a JSON string literal including the
            // surrounding double-quotes, e.g. "\"hello\"" for the value hello.
            // This is a valid C# string literal so it can be spliced directly.
            sb.Append(JsonSerializer.Serialize(values[i]));
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
