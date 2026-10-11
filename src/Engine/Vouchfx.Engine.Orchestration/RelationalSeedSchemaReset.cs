namespace Vouchfx.Engine.Orchestration;

/// <summary>Clears seeded schema objects before applying SQL to a kept topology again.</summary>
internal static class RelationalSeedSchemaReset
{
    private static readonly string[] MySqlSystemDatabases = { "mysql", "sys", "information_schema", "performance_schema" };

    internal static async Task ResetAsync(
        string dependencyName, RelationalStoreKind kind, string connectionString, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var connection = SeedApplier.CreateRelationalConnection(kind, connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            if (kind == RelationalStoreKind.MySql)
            {
                // Use the database the opened driver actually selected, never another database
                // on the server. SHOW CREATE preserves its character set/collation and options.
                var database = connection.Database;
                if (string.IsNullOrWhiteSpace(database) ||
                    MySqlSystemDatabases.Contains(database, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A user database is required for seed reset.");
                }

                var identifier = "`" + database.Replace("`", "``", StringComparison.Ordinal) + "`";
                command.CommandText = "SHOW CREATE DATABASE " + identifier;
                string definition;
                await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                        throw new InvalidOperationException("The seed database definition is unavailable.");
                    definition = reader.GetString(1);
                }

                command.CommandText = "DROP DATABASE " + identifier + "; " + definition + "; USE " + identifier + ";";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // SQL Server cleanup remains inside the selected database, preserving active
                // isolation connections. A transaction also avoids a partial reset on failure.
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                command.Transaction = transaction;
                command.CommandText = kind == RelationalStoreKind.Postgres
                    ? "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"
                    : SqlServerResetSql;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new OrchestrationException(new OrchestrationErrorInfo(
                Kind: OrchestrationErrorKind.Provision,
                ResourceName: dependencyName,
                RegistryHost: null,
                AuthStatus: null,
                Detail: $"re-seed could not reset the '{dependencyName}' schema: {SeedFailureDetails.Describe(error)}"), error);
        }
    }

    // Catalogs below are current-database catalogs. Every catalog-derived identifier is quoted
    // by QUOTENAME. Remove FKs before tables; then honour schema-bound object dependencies.
    private const string SqlServerResetSql = """
        IF DB_NAME() IN ('master', 'model', 'msdb', 'tempdb')
            THROW 50000, 'A user database is required for seed reset.', 1;
        DECLARE @sql nvarchar(max) = N'';
        SELECT @sql += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name)
            + N' SET (SYSTEM_VERSIONING = OFF);' FROM sys.tables WHERE temporal_type = 2;
        EXEC sys.sp_executesql @sql;
        SET @sql = N'';
        SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.'
            + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys;
        EXEC sys.sp_executesql @sql;

        DECLARE @id int, @type char(2), @name nvarchar(517);
        WHILE EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0
            AND type IN ('U', 'V', 'P', 'PC', 'FN', 'FS', 'FT', 'IF', 'TF', 'SN', 'SO', 'AF'))
        BEGIN
            SET @id = NULL;
            SELECT TOP (1) @id = o.object_id, @type = o.type,
                @name = QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name)
                FROM sys.objects o WHERE o.is_ms_shipped = 0
                AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'FS', 'FT', 'IF', 'TF', 'SN', 'SO', 'AF')
                AND NOT EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d
                    JOIN sys.objects r ON r.object_id = d.referencing_id
                    WHERE d.referenced_id = o.object_id AND d.is_schema_bound_reference = 1
                    AND COALESCE(NULLIF(r.parent_object_id, 0), r.object_id) <> o.object_id);
            IF @id IS NULL THROW 50000, 'Seed reset encountered a schema-bound dependency cycle.', 1;
            SET @sql = N'DROP ' + CASE
                WHEN @type = 'U' THEN N'TABLE '
                WHEN @type = 'V' THEN N'VIEW '
                WHEN @type IN ('P', 'PC') THEN N'PROCEDURE '
                WHEN @type = 'SN' THEN N'SYNONYM '
                WHEN @type = 'SO' THEN N'SEQUENCE '
                WHEN @type = 'AF' THEN N'AGGREGATE '
                ELSE N'FUNCTION ' END + @name + N';';
            EXEC sys.sp_executesql @sql;
        END;
        -- Table type columns may use alias/CLR types; remove those dependants first.
        -- Separate catalog-selected batches establish the dependency order without
        -- relying on row order in SELECT's variable concatenation.
        SET @sql = N'';
        SELECT @sql += N'DROP TYPE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.types WHERE is_user_defined = 1 AND is_table_type = 1;
        EXEC sys.sp_executesql @sql;
        SET @sql = N'';
        SELECT @sql += N'DROP TYPE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.types WHERE is_user_defined = 1 AND is_table_type = 0;
        EXEC sys.sp_executesql @sql;
        """;
}
