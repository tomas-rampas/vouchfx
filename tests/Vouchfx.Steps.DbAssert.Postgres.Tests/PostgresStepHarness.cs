// Shared harness for the Npgsql behaviour-pin tests of db-assert.postgres.
//
// Runs the REAL emitted step: provider.Emit -> CsxAssembler -> Roslyn (compile once) ->
// RunIsolatedAsync, exactly as the existing provider tests do. The connection string is staged
// in Vars at run time, so one compilation serves every row of a matrix; an expect.row value of
// the form "{name}" is resolved from Vars by the emitted Substitute_Helpers, so one compilation
// also serves every candidate expectation for the same query.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

/// <summary>A compiled <c>db-assert.postgres</c> step that can be run many times.</summary>
internal sealed class CompiledPostgresStep
{
    private readonly CompiledScript _compiled;
    private readonly string _stepId;

    internal CompiledPostgresStep(CompiledScript compiled, string stepId, string connectionKey)
    {
        _compiled = compiled;
        _stepId = stepId;
        ConnectionKey = connectionKey;
    }

    /// <summary>The <c>Vars</c> key the emitted step reads its connection string from.</summary>
    public string ConnectionKey { get; }

    /// <summary>Runs the compiled step once with the supplied connection string and extra variables.</summary>
    public async Task<StepOutcome> RunAsync(
        string connectionString,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ConnectionKey] = connectionString,
        };
        if (variables is not null)
        {
            foreach (var pair in variables)
            {
                vars[pair.Key] = pair.Value;
            }
        }

        await RoslynScriptCompiler.RunIsolatedAsync(_compiled, new ScriptGlobalVariables(vars));

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(_stepId));
        Assert.True(vars.ContainsKey(outcomeKey), $"Vars must contain the outcome key '{outcomeKey}'.");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }
}

internal static class PostgresStepHarness
{
    private const string Target = "pin-db";

    private sealed class PinCompileContext : ICompileContext
    {
        public PinCompileContext(string stepId) => StepId = stepId;

        public string SuiteDirectory => Directory.GetCurrentDirectory();

        public string StepId { get; }

        public string SuiteNamespace => "Generated";

        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    /// <summary>Compile-time references the emitted helper needs; never loaded into the collectible context.</summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Npgsql.NpgsqlConnection).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    /// <summary>Compiles a step that runs <paramref name="query"/> and asserts the given row and/or row count.</summary>
    public static CompiledPostgresStep Compile(
        string stepId,
        string query,
        IReadOnlyDictionary<string, string>? expectRow,
        int? expectRowCount = null)
    {
        var model = new DbAssertPostgresModel(
            Target: Target,
            Query: query,
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: expectRowCount, Row: expectRow));

        var fragment = new DbAssertPostgresProvider().Emit(model, new PinCompileContext(stepId));
        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);
        return new CompiledPostgresStep(compiled, stepId, VarKeys.Connection(Target));
    }

    /// <summary>
    /// Compiles a step whose single expectation is <c>column = "{expected}"</c>, so that the
    /// candidate text is supplied per run through <c>Vars["expected"]</c>.
    /// </summary>
    public static CompiledPostgresStep CompileSingleColumn(string stepId, string query, string column) =>
        Compile(
            stepId,
            query,
            new Dictionary<string, string>(StringComparer.Ordinal) { [column] = "{expected}" });

    /// <summary>Reads the <c>actual</c> text out of a column-mismatch observation.</summary>
    public static string ActualOf(StepOutcome outcome)
    {
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        using var document = JsonDocument.Parse(outcome.Observation!);
        return document.RootElement.GetProperty("actual").GetString()!;
    }

    private static readonly ConcurrentDictionary<string, CompiledPostgresStep> s_compiled =
        new(StringComparer.Ordinal);

    /// <summary>The single-column "{expected}" step for a rendering case, compiled once per process.</summary>
    public static CompiledPostgresStep CompiledFor(RenderCase renderCase) =>
        s_compiled.GetOrAdd(renderCase.Sql, sql => CompileSingleColumn("pin", sql, "v"));

    /// <summary>
    /// What the step compares for <paramref name="renderCase"/>: runs it with an expectation that
    /// cannot match and reads the <c>actual</c> text out of the failure observation.
    /// </summary>
    public static async Task<string> RenderedAsync(RenderCase renderCase, string connectionString)
    {
        var outcome = await CompiledFor(renderCase).RunAsync(
            connectionString,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = "\u0001 matches nothing" });
        return ActualOf(outcome);
    }

    /// <summary>Runs <paramref name="body"/> with <see cref="CultureInfo.CurrentCulture"/> set, then restores it.</summary>
    public static async Task<T> UnderCultureAsync<T>(string cultureName, Func<Task<T>> body)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            return await body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Normalises the no-break space ICU puts before the AM/PM designator (U+202F, ICU 72 and later)
    /// to an ordinary space, so one literal serves every platform. The step itself does NOT
    /// normalise: on ICU 72+ an expectation written with an ordinary space does not match.
    /// </summary>
    public static string NormaliseSpaces(string text) =>
        text.Replace('\u202f', ' ').Replace('\u00a0', ' ');

    /// <summary>
    /// The CLR type Npgsql reports for column 0 and the CLR type of the value the emitted helper's
    /// non-generic read (<c>reader[col]</c>) returns.
    /// </summary>
    public static async Task<(Type FieldType, object? Value, Exception? ReadError)> ReadClrAsync(string connectionString, string sql)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = sql;
                var reader = await command.ExecuteReaderAsync();
                try
                {
                    Assert.True(await reader.ReadAsync());
                    var fieldType = reader.GetFieldType(0);
                    try
                    {
                        return (fieldType, reader["v"], null);
                    }
                    catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
                    {
                        return (fieldType, null, ex);
                    }
                }
                finally
                {
                    await reader.DisposeAsync();
                }
            }
            finally
            {
                await command.DisposeAsync();
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// The three-part pin for one rendering case under one culture: the text the step reports equals
    /// the pinned literal; that exact text makes <c>expect.row</c> pass; and the alternative
    /// spelling makes it fail, reporting the same actual text.
    /// </summary>
    public static async Task AssertRenderingAsync(RenderCase renderCase, string cultureName, string connectionString)
    {
        await UnderCultureAsync(cultureName, async () =>
        {
            var step = CompiledFor(renderCase);
            var actual = await RenderedAsync(renderCase, connectionString);
            Assert.Equal(renderCase.RenderedUnder(cultureName), NormaliseSpaces(actual));

            var exact = await step.RunAsync(
                connectionString, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = actual });
            Assert.Equal(Verdict.Pass, exact.Verdict);
            Assert.Equal("{\"rowCount\":1}", exact.Observation);

            Assert.NotEqual(renderCase.Alternative, actual);
            var alternative = await step.RunAsync(
                connectionString,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["expected"] = renderCase.Alternative });
            Assert.Equal(actual, ActualOf(alternative));
            return 0;
        });
    }

    /// <summary>Asserts the CLR type Npgsql reports, and the value type (DBNull for SQL NULL).</summary>
    public static async Task AssertClrTypeAsync(RenderCase renderCase, string connectionString)
    {
        var (fieldType, value, error) = await ReadClrAsync(connectionString, renderCase.Sql);
        Assert.Equal(renderCase.ClrType, fieldType);
        if (renderCase.ReadThrows)
        {
            Assert.NotNull(error);
            Assert.IsType<InvalidCastException>(error);
            return;
        }

        Assert.Null(error);
        Assert.Equal(
            renderCase.Rendered[string.Empty] == "null" ? typeof(DBNull) : renderCase.ValueClrType ?? renderCase.ClrType,
            value!.GetType());
    }
}
