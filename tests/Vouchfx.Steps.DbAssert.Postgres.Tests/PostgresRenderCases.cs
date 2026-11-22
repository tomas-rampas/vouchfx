// The value matrix shared by the fake-server (non-Docker) and real-server (Docker) rendering pins.
//
// Every row is ONE query returning ONE value in a column named "v", plus what Npgsql 8.0.9 hands
// back for it through the non-generic read the emitted helper uses (reader[col]) and the exact text
// the helper then compares (value.ToString(), or "null" for SQL NULL) under each culture.
//
// RECORDED DEFECTS. These are pinned as today's behaviour, NOT endorsed, and not fixed here: a
// change to any of them is a product decision, not a test edit. Each has a test marked
// [Trait("pins", "defect")], so `dotnet test --list-tests --filter "pins=defect"` lists them all,
// and each affected row below ends with a "// defect" comment naming the ids. All the tests named
// here are in DbAssertPostgresRenderingPinTests (they run against the fake, without Docker).
//
//   R1  Text depends on the process culture.
//       What: date, timestamp, timestamptz, timetz and numeric render with the current culture, so an
//             expect.row written on one machine can fail on another.
//       Pinned by: CultureDependence_ASuiteWrittenUnderOneCulture_FailsUnderAnother
//       A driver upgrade: unchanged for these types on 9.0.5 and 10.0.4, except that the date rows
//             change type and the time rows become culture-dependent too (see below).
//   R2  Fractional seconds are dropped.
//       What: timestamp, timestamptz and timetz render through DateTime or DateTimeOffset, whose text has
//             no fractional seconds, so a different microsecond value gives the same text and the same
//             expect.row passes for either.
//       Pinned by: FractionalSeconds_AreDropped_SoADifferentMicrosecondValuePasses
//       A driver upgrade: unchanged on 9.0.5 and 10.0.4.
//   R3  bytea and arrays render as CLR type names.
//       What: "System.Byte[]" and "System.Int32[]", so their content is never compared.
//       Pinned by: ByteaAndArrays_RenderAsClrTypeNames_TheirContentIsNeverCompared
//       A driver upgrade: unchanged on 9.0.5 and 10.0.4.
//   R4  An interval with a months part renders as "null".
//       What: the read throws, the helper turns that into the text "null", which is also the text of SQL NULL.
//       Pinned by: Interval_WithAMonthsComponent_IsIndistinguishableFromSqlNull
//       A driver upgrade: unchanged on 9.0.5 and 10.0.4.
//   R5  timetz text carries a date part that means nothing (01/02/0001).
//       What: the value is read as a DateTimeOffset on 0001-01-02, so PostgreSQL's own spelling never matches.
//       Pinned by: TimeTz_Text_CarriesAMeaninglessDatePart
//       A driver upgrade: unchanged on 9.0.5 and 10.0.4.
//   R6  Any exception from the read becomes "null", a misspelt column name included.
//       What: the emitted helper catches everything from reader[column], so an unreadable value and a
//             column that does not exist both pass expect.row "null".
//       Pinned by: UnreadableValueAndMisspeltColumn_BothRenderAsNull
//       A driver upgrade: unchanged (the read is the helper's own code).
//
// What a reviewer measured on a scratch copy (22 November 2026): on Npgsql 9.0.5 nothing here
// changes; on 10.0.4 exactly the date rows (DateTime becomes DateOnly) and the time rows (TimeSpan
// becomes TimeOnly: the text loses its seconds and becomes culture-dependent) fail, on the fake and
// on a real server alike. Those type changes are the reason the driver is held (.github/dependabot.yml).
//
// The TLS weaknesses (T1 one diagnostic for four different causes, T2 the default connection
// encrypts without validating, T3 SSL Mode=Require ignores a provided root) are listed in the header
// of DbAssertPostgresTlsPinDockerTests.cs; they carry the same trait.
using System.Globalization;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

internal sealed record RenderCase(
    string Type,
    string Label,
    string Sql,
    uint Oid,
    Func<byte[]?> Binary,
    Type ClrType,
    IReadOnlyDictionary<string, string> Rendered,
    string Alternative)
{
    /// <summary>The CLR type of the value reader[col] returns when it differs from <see cref="ClrType"/> (the field type).</summary>
    public Type? ValueClrType { get; init; }

    /// <summary>
    /// True when the non-generic read itself throws (Npgsql 8 cannot hand back an interval with a
    /// months component as a TimeSpan). The emitted helper swallows that exception and compares the
    /// text "null" - the same text it uses for SQL NULL.
    /// </summary>
    public bool ReadThrows { get; init; }

    public override string ToString() => $"{Type} / {Label}";

    /// <summary>Rendered text under the culture named <paramref name="culture"/> ("" is the invariant culture).</summary>
    public string RenderedUnder(string culture) => Rendered[culture];
}

internal static class PostgresRenderCases
{
    /// <summary>The cultures the engine can plausibly run under; "" is <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public static readonly string[] Cultures = { "en-US", "en-GB", "cs-CZ", string.Empty };

    private static Dictionary<string, string> Same(string text) =>
        Cultures.ToDictionary(c => c, _ => text, StringComparer.Ordinal);

    private static Dictionary<string, string> PerCulture(string enUs, string enGb, string csCz, string invariant) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en-US"] = enUs,
            ["en-GB"] = enGb,
            ["cs-CZ"] = csCz,
            [string.Empty] = invariant,
        };

    private static RenderCase Case(
        string type,
        string label,
        string literal,
        uint oid,
        Func<byte[]?> binary,
        Type clr,
        IReadOnlyDictionary<string, string> rendered) =>
        new(type, label, $"SELECT {literal} AS v", oid, binary, clr, rendered, s_alternatives[type + " / " + label]);

    /// <summary>
    /// A plausible alternative spelling an author might write instead of the exact text: usually the
    /// form PostgreSQL's own text output (and so psql) would show. Each must FAIL against the step.
    /// </summary>
    private static readonly Dictionary<string, string> s_alternatives = new(StringComparer.Ordinal)
    {
        ["date / single-digit day and month"] = "2024-01-05",
        ["date / before 1970"] = "1969-12-31",
        ["date / NULL"] = "NULL",
        ["time / zero time of day"] = "00:00:00.000000",
        ["time / fractional seconds"] = "13:45:30.123456",
        ["time / NULL"] = "NULL",
        ["time with time zone / zero time of day, UTC"] = "00:00:00+00",
        ["time with time zone / fractional seconds, +02"] = "13:45:30.123456+02",
        ["time with time zone / NULL"] = "NULL",
        ["timestamp / zero time of day"] = "2024-01-05 00:00:00",
        ["timestamp / fractional seconds"] = "2024-01-05 13:45:30.123456",
        ["timestamp / before 1970"] = "1969-12-31 23:59:59",
        ["timestamp / NULL"] = "NULL",
        ["timestamp with time zone / zero time of day, UTC"] = "2024-01-05 00:00:00+00",
        ["timestamp with time zone / fractional seconds, +02"] = "2024-01-05T11:45:30.123456Z",
        ["timestamp with time zone / NULL"] = "NULL",
        ["interval / zero"] = "0",
        ["interval / days and fractional seconds"] = "1 day 02:03:04.5",
        ["interval / months, days and time"] = "1 mon 2 days 03:04:05",
        ["interval / NULL"] = "NULL",
        ["numeric / trailing zeros"] = "12.34",
        ["numeric / negative fraction"] = "-.5000",
        ["numeric / NULL"] = "NULL",
        ["boolean / true"] = "true",
        ["boolean / NULL"] = "NULL",
        ["uuid / value"] = "0F8FAD5B-D9CB-469F-A165-70867728950E",
        ["bytea / value"] = "\\xdeadbeef",
        ["json / value"] = "{\"a\":1,\"b\":[true]}",
        ["jsonb / value"] = "{\"a\":1,\"b\":[true]}",
        ["integer[] / value"] = "{1,2,3}",
    };

    public static IReadOnlyList<RenderCase> All { get; } = Build();

    /// <summary>The same timestamp as "timestamp / fractional seconds" but 530865 microseconds later (R2).</summary>
    public const string TimestampOtherMicrosecondsSql = "SELECT '2024-01-05 13:45:30.654321'::timestamp AS v";

    /// <summary>A bytea with different content from "bytea / value" (R3).</summary>
    public const string ByteaOtherContentSql = "SELECT '\\xcafebabe'::bytea AS v";

    /// <summary>
    /// Extra canned results for the defect tests that need a second value beside a matrix row. They are
    /// not matrix rows: the rendering matrix and its Docker twin stay one row per behaviour.
    /// </summary>
    public static IReadOnlyDictionary<string, FakeColumn> ExtraResults { get; } =
        new Dictionary<string, FakeColumn>(StringComparer.Ordinal)
        {
            [TimestampOtherMicrosecondsSql] = new FakeColumn(
                "v", PgBinary.OidTimestamp,
                PgBinary.Timestamp(new DateTime(2024, 1, 5, 13, 45, 30).AddTicks(6543210))),
            [ByteaOtherContentSql] = new FakeColumn("v", PgBinary.OidBytea, new byte[] { 0xca, 0xfe, 0xba, 0xbe }),
        };

    /// <summary>Theory rows carry the case NAME (serialisable, so each row is its own test).</summary>
    public static IEnumerable<object[]> Theory() => All.Select(c => new object[] { c.ToString() });

    public static RenderCase Find(string name) => All.Single(c => c.ToString() == name);

    private static List<RenderCase> Build()
    {
        var cases = new List<RenderCase>
        {
            Case("date", "single-digit day and month", "'2024-01-05'::date", PgBinary.OidDate, () => PgBinary.Date(2024, 1, 5), typeof(DateTime), PerCulture("1/5/2024 12:00:00 AM", "05/01/2024 00:00:00", "05.01.2024 0:00:00", "01/05/2024 00:00:00")), // defect R1: see table
            Case("date", "before 1970", "'1969-12-31'::date", PgBinary.OidDate, () => PgBinary.Date(1969, 12, 31), typeof(DateTime), PerCulture("12/31/1969 12:00:00 AM", "31/12/1969 00:00:00", "31.12.1969 0:00:00", "12/31/1969 00:00:00")), // defect R1: see table
            Case("date", "NULL", "NULL::date", PgBinary.OidDate, () => null, typeof(DateTime), Same("null")),
            Case("time", "zero time of day", "'00:00:00'::time", PgBinary.OidTime, () => PgBinary.Time(0), typeof(TimeSpan), Same("00:00:00")),
            Case("time", "fractional seconds", "'13:45:30.123456'::time", PgBinary.OidTime, () => PgBinary.Time(((13L * 3600) + (45 * 60) + 30) * 1_000_000 + 123456), typeof(TimeSpan), Same("13:45:30.1234560")),
            Case("time", "NULL", "NULL::time", PgBinary.OidTime, () => null, typeof(TimeSpan), Same("null")),
            Case("time with time zone", "zero time of day, UTC", "'00:00:00+00'::timetz", PgBinary.OidTimeTz, () => PgBinary.TimeTz(0, 0), typeof(DateTimeOffset), PerCulture("1/2/0001 12:00:00 AM +00:00", "02/01/0001 00:00:00 +00:00", "02.01.0001 0:00:00 +00:00", "01/02/0001 00:00:00 +00:00")), // defect R1, R2, R5: see table
            Case("time with time zone", "fractional seconds, +02", "'13:45:30.123456+02'::timetz", PgBinary.OidTimeTz, () => PgBinary.TimeTz(((13L * 3600) + (45 * 60) + 30) * 1_000_000 + 123456, -7200), typeof(DateTimeOffset), PerCulture("1/2/0001 1:45:30 PM +02:00", "02/01/0001 13:45:30 +02:00", "02.01.0001 13:45:30 +02:00", "01/02/0001 13:45:30 +02:00")), // defect R1, R2, R5: see table
            Case("time with time zone", "NULL", "NULL::timetz", PgBinary.OidTimeTz, () => null, typeof(DateTimeOffset), Same("null")),
            Case("timestamp", "zero time of day", "'2024-01-05 00:00:00'::timestamp", PgBinary.OidTimestamp, () => PgBinary.Timestamp(new DateTime(2024, 1, 5)), typeof(DateTime), PerCulture("1/5/2024 12:00:00 AM", "05/01/2024 00:00:00", "05.01.2024 0:00:00", "01/05/2024 00:00:00")), // defect R1, R2: see table
            Case("timestamp", "fractional seconds", "'2024-01-05 13:45:30.123456'::timestamp", PgBinary.OidTimestamp, () => PgBinary.Timestamp(new DateTime(2024, 1, 5, 13, 45, 30).AddTicks(1234560)), typeof(DateTime), PerCulture("1/5/2024 1:45:30 PM", "05/01/2024 13:45:30", "05.01.2024 13:45:30", "01/05/2024 13:45:30")), // defect R1, R2: see table
            Case("timestamp", "before 1970", "'1969-12-31 23:59:59'::timestamp", PgBinary.OidTimestamp, () => PgBinary.Timestamp(new DateTime(1969, 12, 31, 23, 59, 59)), typeof(DateTime), PerCulture("12/31/1969 11:59:59 PM", "31/12/1969 23:59:59", "31.12.1969 23:59:59", "12/31/1969 23:59:59")), // defect R1, R2: see table
            Case("timestamp", "NULL", "NULL::timestamp", PgBinary.OidTimestamp, () => null, typeof(DateTime), Same("null")),
            Case("timestamp with time zone", "zero time of day, UTC", "'2024-01-05 00:00:00+00'::timestamptz", PgBinary.OidTimestampTz, () => PgBinary.Timestamp(new DateTime(2024, 1, 5)), typeof(DateTime), PerCulture("1/5/2024 12:00:00 AM", "05/01/2024 00:00:00", "05.01.2024 0:00:00", "01/05/2024 00:00:00")), // defect R1, R2: see table
            Case("timestamp with time zone", "fractional seconds, +02", "'2024-01-05 13:45:30.123456+02'::timestamptz", PgBinary.OidTimestampTz, () => PgBinary.Timestamp(new DateTime(2024, 1, 5, 11, 45, 30).AddTicks(1234560)), typeof(DateTime), PerCulture("1/5/2024 11:45:30 AM", "05/01/2024 11:45:30", "05.01.2024 11:45:30", "01/05/2024 11:45:30")), // defect R1, R2: see table
            Case("timestamp with time zone", "NULL", "NULL::timestamptz", PgBinary.OidTimestampTz, () => null, typeof(DateTime), Same("null")),
            Case("interval", "zero", "'0'::interval", PgBinary.OidInterval, () => PgBinary.Interval(0, 0, 0), typeof(TimeSpan), Same("00:00:00")),
            Case("interval", "days and fractional seconds", "'1 day 02:03:04.5'::interval", PgBinary.OidInterval, () => PgBinary.Interval(((2L * 3600) + (3 * 60) + 4) * 1_000_000 + 500_000, 1, 0), typeof(TimeSpan), Same("1.02:03:04.5000000")),
            Case("interval", "months, days and time", "'1 mon 2 days 03:04:05'::interval", PgBinary.OidInterval, () => PgBinary.Interval(((3L * 3600) + (4 * 60) + 5) * 1_000_000, 2, 1), typeof(TimeSpan), Same("null")) with { ReadThrows = true }, // defect R4: see table
            Case("interval", "NULL", "NULL::interval", PgBinary.OidInterval, () => null, typeof(TimeSpan), Same("null")),
            Case("numeric", "trailing zeros", "12.3400::numeric", PgBinary.OidNumeric, () => PgBinary.Numeric("12.3400"), typeof(decimal), PerCulture("12.3400", "12.3400", "12,3400", "12.3400")), // defect R1: see table
            Case("numeric", "negative fraction", "'-0.5000'::numeric", PgBinary.OidNumeric, () => PgBinary.Numeric("-0.5000"), typeof(decimal), PerCulture("-0.5000", "-0.5000", "-0,5000", "-0.5000")), // defect R1: see table
            Case("numeric", "NULL", "NULL::numeric", PgBinary.OidNumeric, () => null, typeof(decimal), Same("null")),
            Case("boolean", "true", "true", PgBinary.OidBool, () => PgBinary.Boolean(true), typeof(bool), Same("True")),
            Case("boolean", "NULL", "NULL::boolean", PgBinary.OidBool, () => null, typeof(bool), Same("null")),
            Case("uuid", "value", "'0f8fad5b-d9cb-469f-a165-70867728950e'::uuid", PgBinary.OidUuid, () => PgBinary.Uuid(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")), typeof(Guid), Same("0f8fad5b-d9cb-469f-a165-70867728950e")),
            Case("bytea", "value", "'\\xdeadbeef'::bytea", PgBinary.OidBytea, () => new byte[] { 0xde, 0xad, 0xbe, 0xef }, typeof(byte[]), Same("System.Byte[]")), // defect R3: see table
            Case("json", "value", "'{\"a\": 1,  \"b\": [true]}'::json", PgBinary.OidJson, () => PgBinary.Text("{\"a\": 1,  \"b\": [true]}"), typeof(string), Same("{\"a\": 1,  \"b\": [true]}")),
            Case("jsonb", "value", "'{\"b\": [true],  \"a\": 1}'::jsonb", PgBinary.OidJsonb, () => PgBinary.Jsonb("{\"a\": 1, \"b\": [true]}"), typeof(string), Same("{\"a\": 1, \"b\": [true]}")),
            Case("integer[]", "value", "'{1,2,3}'::int4[]", PgBinary.OidInt4Array, () => PgBinary.Int4Array(1, 2, 3), typeof(Array), Same("System.Int32[]")) with { ValueClrType = typeof(int[]) }, // defect R3: see table
        };
        return cases;
    }
}
