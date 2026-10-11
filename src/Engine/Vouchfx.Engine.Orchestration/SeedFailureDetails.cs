using System.Globalization;

namespace Vouchfx.Engine.Orchestration;

/// <summary>Report-safe seed error identifiers. Messages may contain fixture text or host paths.</summary>
internal static class SeedFailureDetails
{
    internal static string Describe(Exception error) => error switch
    {
        Npgsql.PostgresException postgres when IsSqlState(postgres.SqlState) =>
            $"PostgresException (SQLSTATE {postgres.SqlState})",
        Microsoft.Data.SqlClient.SqlException sqlServer =>
            "SqlException (number " + sqlServer.Number.ToString(CultureInfo.InvariantCulture) + ")",
        MySqlConnector.MySqlException mysql =>
            "MySqlException (number " + mysql.Number.ToString(CultureInfo.InvariantCulture) + ")",
        _ => error.GetType().Name,
    };

    private static bool IsSqlState(string value) =>
        value.Length == 5 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z');
}
