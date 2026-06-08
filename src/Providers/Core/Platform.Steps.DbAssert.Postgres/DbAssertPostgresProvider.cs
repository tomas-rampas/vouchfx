// Platform.Steps.DbAssert.Postgres — db-assert.postgres step provider (DSL §5, §13.10).
//
// F-01 implementation: IStepProvider + IStepBinder<T> + IStepValidator<T>.
// IStepCompiler<T> and IResourceContributor<T> are added in F-02.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, query,
//     parameters, expect).  The type const discriminator is injected by the
//     SchemaComposer from Kind — never from the fragment text.
using System.Text.Json;
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
/// F-01 scope: bind, validate, and schema.  Emit (<see cref="IStepCompiler{TModel}"/>)
/// and resource declaration (<see cref="IResourceContributor{TModel}"/>) are
/// delivered in F-02.
/// </para>
/// </remarks>
[StepProvider]
public sealed class DbAssertPostgresProvider
    : IStepProvider,
      IStepBinder<DbAssertPostgresModel>,
      IStepValidator<DbAssertPostgresModel>
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

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }
}
