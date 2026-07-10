// Vouchfx.Steps.DbAssert.SqlServer — db-assert.sqlserver step provider (DSL §5, §13.10).
//
// SQL Server analogue of db-assert.postgres.  Same intent and model shape;
// different client (Microsoft.Data.SqlClient instead of Npgsql).
//
// Substitution model (S04-B-03, H1 security fix — blast-radius containment):
//   • Parameter VALUES    — Substitute_Helpers.Resolve(Vars, …); the resolved value is
//     passed to cmd.Parameters.AddWithValue (parameterised SQL — never concatenated).
//   • Expect-row VALUES   — Substitute_Helpers.Resolve(Vars, …); compared in-memory.
//   • SQL query TEXT      — emitted as a raw template literal; inside ExecuteAsync the
//     helper calls Substitute_Helpers.ResolveIdentifier(Vars, …) in its own try so that
//     an unsafe resolved value writes Verdict.EnvironmentError for this step only (not a
//     scenario-level abort).  Each resolved value is validated against [A-Za-z0-9_.]
//     before being spliced into the query text.  This permits dynamic table/schema
//     identifiers (DSL §6.2) while blocking injection.  Untrusted or non-identifier data
//     MUST be bound through a SQL parameter instead.
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
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

// SubstituteHelper.Source is sourced from Vouchfx.Sdk (S04-B-03).

namespace Vouchfx.Steps.DbAssert.SqlServer;

/// <summary>
/// Core provider for the <c>db-assert.sqlserver</c> step kind (DSL §5).
/// Executes a parameterised SQL query against a declared SQL Server dependency
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
/// CSX executes a parameterised SqlClient query, evaluates row-count and/or column-value
/// expectations, and writes a typed <see cref="StepOutcome"/> into <c>Vars</c> for
/// the runner to read after execution (§13.3.1).  The SQL query text is emitted as a
/// raw template literal; the helper resolves <c>{placeholder}</c> substitution inside
/// its own <c>try</c> (via <c>Substitute_Helpers.ResolveIdentifier</c>), limiting the
/// blast radius of an unsafe resolved value to a step-scoped
/// <see cref="Verdict.EnvironmentError"/> rather than a scenario-level abort.
/// Parameter values and expect-row values support arbitrary substitution (via
/// <c>Substitute_Helpers.Resolve</c>) because parameters are bound via
/// <c>AddWithValue</c> (never concatenated into SQL) and expect values are compared
/// in-memory only.
/// </para>
/// </remarks>
[StepProvider]
public sealed class DbAssertSqlServerProvider
    : IStepProvider,
      IStepBinder<DbAssertSqlServerModel>,
      IStepValidator<DbAssertSqlServerModel>,
      IStepCompiler<DbAssertSqlServerModel>,
      IResourceContributor<DbAssertSqlServerModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("db-assert", "sqlserver");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<DbAssertSqlServerModel> ───────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>db-assert.sqlserver</c>
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
              "description": "Logical name of the sqlserver dependency to query, as declared under environment.dependencies.",
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
    public DbAssertSqlServerModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new DbAssertSqlServerModel(
                Target: string.Empty,
                Query: string.Empty,
                Parameters: null,
                Expect: new SqlServerExpectation(RowCount: null, Row: null));
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

        return new DbAssertSqlServerModel(
            Target: target,
            Query: query,
            Parameters: parameters,
            Expect: new SqlServerExpectation(RowCount: rowCount, Row: row));
    }

    // ── IStepValidator<DbAssertSqlServerModel> ────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(DbAssertSqlServerModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("db-assert.sqlserver: 'target' must not be empty.");

        // (b) query must not be empty.
        if (string.IsNullOrWhiteSpace(model.Query))
            errors.Add("db-assert.sqlserver: 'query' must not be empty.");

        // (c) expect must declare at least one assertion.
        if (model.Expect.RowCount is null
            && (model.Expect.Row is null || model.Expect.Row.Count == 0))
        {
            errors.Add(
                "db-assert.sqlserver: 'expect' must specify rowCount and/or row.");
        }

        // (d) dependency reconciliation: target must name a declared sqlserver dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"db-assert.sqlserver: 'target' '{model.Target}' is not a " +
                    "sqlserver dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"db-assert.sqlserver: 'target' '{model.Target}' is not a " +
                    "sqlserver dependency declared in environment.dependencies.");
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
            "Microsoft.Data.SqlClient",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>DbAssertSqlServer_</c> to prevent collisions when
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
        "static class DbAssertSqlServer_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Executes a parameterised SQL query via Microsoft.Data.SqlClient, evaluates\n" +
        "    /// the row-count and/or column-value expectations, and writes a typed\n" +
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
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        // Read the connection string staged by the orchestrator (VarKeys.Connection pattern).\n" +
        "        // A null or empty string means the dependency was not discovered → EnvironmentError (§12.1).\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"connection string not found for key '\" + connKey + \"'\") + \"}\" );\n" +
        "            return;\n" +
        "        }\n" +
        "        // H1 security fix (blast-radius containment): resolve the query template INSIDE\n" +
        "        // its own try so an unsafe resolved value (non-identifier) writes EnvironmentError\n" +
        "        // for THIS step only and returns normally — subsequent steps still run.\n" +
        "        // Callers MUST NOT invoke ResolveIdentifier outside a try; exceptions must map to\n" +
        "        // their error verdict (this is the canonical pattern for db-assert.sqlserver).\n" +
        "        string resolvedQuery;\n" +
        "        try\n" +
        "        {\n" +
        "            resolvedQuery = Substitute_Helpers.ResolveIdentifier(vars, query);\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Microsoft.Data.SqlClient.SqlConnection? conn = null;\n" +
        "        try\n" +
        "        {\n" +
        "            conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);\n" +
        "            await conn.OpenAsync().ConfigureAwait(false);\n" +
        "            var cmd = conn.CreateCommand();\n" +
        "            try\n" +
        "            {\n" +
        "                cmd.CommandText = resolvedQuery;\n" +
        "                for (int i = 0; i < paramNames.Length; i++)\n" +
        "                {\n" +
        "                    cmd.Parameters.AddWithValue(\"@\" + paramNames[i], (object)paramValues[i]);\n" +
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
        "                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                        observation = failObservation;\n" +
        "                    }\n" +
        "                    else\n" +
        "                    {\n" +
        "                        verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
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
        "        catch (Microsoft.Data.SqlClient.SqlException ex)\n" +
        "        {\n" +
        "            // SqlClient-specific exception: network failure, auth error, etc. = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection or protocol failure = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            conn?.Dispose();  // explicit Dispose() in finally (§13.3.1).\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Redacts credential material from an exception message (§17 — no secrets in observations).\n" +
        "    /// Removes: (1) the full connection string if it appears literally;\n" +
        "    ///          (2) ADO-style Password=/Pwd= key-value pairs.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactCredentials(string connStr, string message)\n" +
        "    {\n" +
        "        if (!string.IsNullOrEmpty(connStr))\n" +
        "            message = message.Replace(connStr, \"***\", System.StringComparison.Ordinal);\n" +
        "        message = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            message,\n" +
        "            \"(?:Password|Pwd)\\\\s*=\\\\s*[^;]+\",\n" +
        "            \"Password=***\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return message;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<DbAssertSqlServerModel> ─────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution opens a SqlClient connection keyed by
    /// <c>VarKeys.Connection(model.Target)</c>, runs <c>model.Query</c> with the
    /// declared parameters bound as <c>SqlParameter</c> instances via
    /// <c>AddWithValue</c> (parameterised SQL — parameter values are never
    /// concatenated into the query text), evaluates the <c>expect.rowCount</c>
    /// and/or <c>expect.row</c> assertions, and writes a typed
    /// <see cref="StepOutcome"/> into
    /// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read
    /// after the script returns.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full <c>static class DbAssertSqlServer_Helpers</c> definition; byte-identical across instances.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block; no <c>using var</c>.</item>
    ///   <item>Model values are emitted as <c>JsonSerializer.Serialize</c>-escaped C# string literals.</item>
    ///   <item>The step id is sanitised via <c>CsxFragment.SanitiseId</c> before splicing.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(DbAssertSqlServerModel model, ICompileContext ctx)
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

        // S04-B-03 / H1 (security fix — blast-radius containment):
        // Pass the raw query template as a plain string literal to ExecuteAsync.
        // ResolveIdentifier is called INSIDE the helper, inside its own try, so a
        // non-identifier placeholder value writes Verdict.EnvironmentError for THIS step
        // only and returns normally — subsequent steps continue to run.
        var resolvedQuery = JsonSerializer.Serialize(model.Query);

        // S04-B-03: wrap each parameter VALUE in Substitute_Helpers.Resolve.  Parameter
        // NAMES are SQL identifiers and are not subject to substitution.
        var resolvedParamValues = new string[paramValues.Length];
        for (int i = 0; i < paramValues.Length; i++)
        {
            resolvedParamValues[i] = $"Substitute_Helpers.Resolve(Vars, {JsonSerializer.Serialize(paramValues[i])})";
        }

        // S04-B-03: wrap each expected-column VALUE in Substitute_Helpers.Resolve.
        var resolvedExpectValues = new string[expectValues.Length];
        for (int i = 0; i < expectValues.Length; i++)
        {
            resolvedExpectValues[i] = $"Substitute_Helpers.Resolve(Vars, {JsonSerializer.Serialize(expectValues[i])})";
        }

        var paramNamesLiteral = BuildStringArrayLiteral(paramNames);
        var paramValuesLiteral = BuildResolvedArrayLiteral(resolvedParamValues);
        var expectColumnsLiteral = BuildStringArrayLiteral(expectColumns);
        var expectValuesLiteral = BuildResolvedArrayLiteral(resolvedExpectValues);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        var block = $$"""
            {
                await DbAssertSqlServer_Helpers.ExecuteAsync(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{resolvedQuery}},
                    {{paramNamesLiteral}},
                    {{paramValuesLiteral}},
                    {{rowCountLiteral}},
                    {{expectColumnsLiteral}},
                    {{expectValuesLiteral}});
            }
            """;

        // Add SubstituteHelper.Source to RequiredHelpers (B-03).
        // CsxAssembler deduplicates by class name so it is included at most once.
        var helpers = new List<string>(s_helpers) { SubstituteHelper.Source };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<DbAssertSqlServerModel> ──────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="ResourceRequirement"/> with
    /// <c>Family="sqlserver"</c> and <c>Name=model.Target</c>.
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(DbAssertSqlServerModel model)
    {
        yield return new ResourceRequirement(
            Family: "sqlserver",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>Microsoft.Data.SqlClient</c> assembly so the Roslyn compiler
    /// can resolve <c>SqlConnection</c> and related types in the emitted helper class.
    /// The assembly is already loaded in the Default ALC (the provider project
    /// references it directly) and must never be loaded into the collectible ALC
    /// (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly;
        }
    }

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is one of the
    /// <c>db-assert.sqlserver</c> Fail-observation shapes that this provider can render
    /// as an expected-vs-observed diff.
    /// </summary>
    /// <remarks>
    /// Recognised shapes (emitted by <c>DbAssertSqlServer_Helpers</c> on a Fail verdict):
    /// <list type="bullet">
    ///   <item><description><c>{"column":…,"expected":…,"actual":…}</c> — a column-value mismatch.</description></item>
    ///   <item><description><c>{"rowCount":{"expected":…,"actual":…}}</c> — a row-count mismatch.</description></item>
    /// </list>
    /// </remarks>
    /// <inheritdoc cref="IStepDiffRenderer.CanRender" />
    public bool CanRender(JsonElement observation) =>
        TryReadColumnDiff(observation, out _, out _, out _)
        || TryReadRowCountDiff(observation, out _, out _);

    /// <inheritdoc cref="IStepDiffRenderer.RenderDiff" />
    public string? RenderDiff(JsonElement observation)
    {
        if (TryReadColumnDiff(observation, out var column, out var expected, out var actual))
        {
            return RenderColumnTable(column, expected, actual);
        }

        if (TryReadRowCountDiff(observation, out var expectedCount, out var actualCount))
        {
            return RenderRowCountTable(expectedCount, actualCount);
        }

        return null;
    }

    // ── IStepDiffRenderer helpers ─────────────────────────────────────────────

    private static bool TryReadColumnDiff(
        JsonElement observation,
        out string column,
        out string expected,
        out string actual)
    {
        column = string.Empty;
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("column", out var columnEl)
            || columnEl.ValueKind != JsonValueKind.String
            || !observation.TryGetProperty("expected", out var expectedEl)
            || !observation.TryGetProperty("actual", out var actualEl))
        {
            return false;
        }

        column = columnEl.GetString() ?? string.Empty;
        expected = ScalarText(expectedEl);
        actual = ScalarText(actualEl);
        return true;
    }

    private static bool TryReadRowCountDiff(
        JsonElement observation,
        out string expected,
        out string actual)
    {
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("rowCount", out var rowCountEl)
            || rowCountEl.ValueKind != JsonValueKind.Object
            || !rowCountEl.TryGetProperty("expected", out var expectedEl)
            || !rowCountEl.TryGetProperty("actual", out var actualEl))
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

    private static string RenderColumnTable(string column, string expected, string actual)
    {
        var headers = new[] { "column", "expected", "actual" };
        var values = new[] { column, expected, actual };
        return RenderTable(headers, values);
    }

    private static string RenderRowCountTable(string expected, string actual)
    {
        var headers = new[] { "rowCount", "expected", "actual" };
        var values = new[] { "(rows)", expected, actual };
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
        {
            return "new string[] { }";
        }

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
        {
            return "new string[] { }";
        }

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
