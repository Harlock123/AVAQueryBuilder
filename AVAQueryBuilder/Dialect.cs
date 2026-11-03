using System.Collections.Generic;
using System.Linq;

namespace AVAQueryBuilder;

/// <summary>
/// The parts of the SQL that differ between engines: row limits, alias quoting, casts and the derived-field
/// functions. <see cref="QueryBuilder"/> and the dialogs' previews ask here instead of writing T-SQL directly.
/// </summary>
public static class Dialect
{
    /// <summary>A column alias, quoted: [alias] for SQL Server, "alias" for the others.</summary>
    public static string Alias(DbEngine engine, string alias) => engine == DbEngine.SqlServer
        ? $"[{alias.Replace("]", "]]")}]"
        : $"\"{alias.Replace("\"", "\"\"")}\"";

    /// <summary>" AS alias", or nothing when there is no alias.</summary>
    public static string As(DbEngine engine, string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? string.Empty : $" AS {Alias(engine, alias)}";

    /// <summary>True when the row limit goes after SELECT (TOP n); false when it ends the query (LIMIT n).</summary>
    public static bool LimitsWithTop(DbEngine engine) => engine == DbEngine.SqlServer;

    /// <summary>The first <paramref name="rows"/> rows of a table, for the field browser.</summary>
    public static string Preview(DbEngine engine, string table, int rows) => LimitsWithTop(engine)
        ? $"SELECT TOP {rows} * FROM {table}"
        : $"SELECT * FROM {table} LIMIT {rows}";

    /// <summary>
    /// A filter's "cast as" choice (VARCHAR, INT, DATE, DATETIME, DECIMAL) applied to a field. SQLite has no
    /// date type, so dates go through its DATE() and DATETIME() functions instead of CAST.
    /// </summary>
    public static string Cast(DbEngine engine, string field, string castAs) => (engine, castAs) switch
    {
        (DbEngine.SqlServer, "VARCHAR") => $"CAST({field} AS VARCHAR(MAX))",
        (DbEngine.PostgreSql, "DATETIME") => $"CAST({field} AS TIMESTAMP)",
        (DbEngine.Sqlite, "VARCHAR") => $"CAST({field} AS TEXT)",
        (DbEngine.Sqlite, "INT") => $"CAST({field} AS INTEGER)",
        (DbEngine.Sqlite, "DECIMAL") => $"CAST({field} AS NUMERIC)",
        (DbEngine.Sqlite, "DATE") => $"DATE({field})",
        (DbEngine.Sqlite, "DATETIME") => $"DATETIME({field})",
        _ => $"CAST({field} AS {castAs})"
    };

    /// <summary>The derivations this engine can write. SQLite has no REVERSE, so it doesn't offer it.</summary>
    public static List<string> Derivations(DbEngine engine) => engine == DbEngine.Sqlite
        ? DerivedFieldViewModel.Derivations.Where(d => d != "Reverse").ToList()
        : DerivedFieldViewModel.Derivations;

    /// <summary>
    /// A derived field's SQL. The parameter is SQL the user typed (a number, a column, or a date expression in
    /// the engine's own syntax, such as GETDATE(), NOW() or DATE('now')).
    /// </summary>
    public static string Derive(DbEngine engine, string field, string derivation, string parameter) => engine switch
    {
        DbEngine.PostgreSql => DerivePostgres(field, derivation, parameter),
        DbEngine.Sqlite => DeriveSqlite(field, derivation, parameter),
        _ => DeriveSqlServer(field, derivation, parameter)
    };

    private static string DeriveSqlServer(string field, string derivation, string p) => derivation switch
    {
        "UPPER CASE" => $"UPPER({field})",
        "lower case" => $"LOWER({field})",
        "Trim" => $"TRIM({field})",
        "Length" => $"LEN({field})",
        "Left N Chars" => $"LEFT({field}, {p})",
        "Right N Chars" => $"RIGHT({field}, {p})",
        "Reverse" => $"REVERSE({field})",
        "Year Part" => $"DATEPART(YEAR, {field})",
        "Month Part" => $"DATEPART(MONTH, {field})",
        "Day Part" => $"DATEPART(DAY, {field})",
        "Hour Part" => $"DATEPART(HOUR, {field})",
        "Month Name" => $"DATENAME(MONTH, {field})",
        "Day Name" => $"DATENAME(WEEKDAY, {field})",
        "Date Only" => $"CAST({field} AS DATE)",
        "DATEDIFF (Days)" => $"DATEDIFF(DAY, {field}, {p})",
        "DATEDIFF (Months)" => $"DATEDIFF(MONTH, {field}, {p})",
        "DATEDIFF (Years)" => $"DATEDIFF(YEAR, {field}, {p})",
        "DATEADD (Days)" => $"DATEADD(DAY, {p}, {field})",
        "DATEADD (Months)" => $"DATEADD(MONTH, {p}, {field})",
        "DATEADD (Years)" => $"DATEADD(YEAR, {p}, {field})",
        "ISNULL (Default)" => $"ISNULL({field}, '{p}')",
        "COALESCE (Fallback)" => $"COALESCE({field}, {p})",
        "Absolute Value" => $"ABS({field})",
        "Round to N Decimals" => $"ROUND({field}, {p})",
        "Ceiling" => $"CEILING({field})",
        "Floor" => $"FLOOR({field})",
        _ => field
    };

    // PostgreSQL's string functions take text only, so non-text columns are cast first. DATEDIFF counts
    // boundaries crossed, as SQL Server does: whole calendar months or years, not elapsed time.
    private static string DerivePostgres(string field, string derivation, string p)
    {
        var text = $"CAST({field} AS TEXT)";
        var ts = $"CAST({field} AS TIMESTAMP)";
        var pts = $"CAST({p} AS TIMESTAMP)";
        return derivation switch
        {
            "UPPER CASE" => $"UPPER({text})",
            "lower case" => $"LOWER({text})",
            "Trim" => $"TRIM({text})",
            "Length" => $"LENGTH({text})",
            "Left N Chars" => $"LEFT({text}, {p})",
            "Right N Chars" => $"RIGHT({text}, {p})",
            "Reverse" => $"REVERSE({text})",
            "Year Part" => $"CAST(EXTRACT(YEAR FROM {ts}) AS INTEGER)",
            "Month Part" => $"CAST(EXTRACT(MONTH FROM {ts}) AS INTEGER)",
            "Day Part" => $"CAST(EXTRACT(DAY FROM {ts}) AS INTEGER)",
            "Hour Part" => $"CAST(EXTRACT(HOUR FROM {ts}) AS INTEGER)",
            "Month Name" => $"TO_CHAR({ts}, 'FMMonth')",
            "Day Name" => $"TO_CHAR({ts}, 'FMDay')",
            "Date Only" => $"CAST({field} AS DATE)",
            "DATEDIFF (Days)" => $"(CAST({p} AS DATE) - CAST({field} AS DATE))",
            "DATEDIFF (Months)" =>
                $"CAST((EXTRACT(YEAR FROM {pts}) - EXTRACT(YEAR FROM {ts})) * 12 + EXTRACT(MONTH FROM {pts}) - EXTRACT(MONTH FROM {ts}) AS INTEGER)",
            "DATEDIFF (Years)" => $"CAST(EXTRACT(YEAR FROM {pts}) - EXTRACT(YEAR FROM {ts}) AS INTEGER)",
            "DATEADD (Days)" => $"({ts} + ({p}) * INTERVAL '1 day')",
            "DATEADD (Months)" => $"({ts} + ({p}) * INTERVAL '1 month')",
            "DATEADD (Years)" => $"({ts} + ({p}) * INTERVAL '1 year')",
            "ISNULL (Default)" => $"COALESCE({field}, '{p}')",
            "COALESCE (Fallback)" => $"COALESCE({field}, {p})",
            "Absolute Value" => $"ABS({field})",
            "Round to N Decimals" => $"ROUND(CAST({field} AS NUMERIC), {p})",
            "Ceiling" => $"CEILING({field})",
            "Floor" => $"FLOOR({field})",
            _ => field
        };
    }

    // SQLite keeps dates as text and reads them with STRFTIME; it has no month or day names, so those are CASEs.
    private static string DeriveSqlite(string field, string derivation, string p)
    {
        string Part(string fmt, string of) => $"CAST(STRFTIME('{fmt}', {of}) AS INTEGER)";
        return derivation switch
        {
            "UPPER CASE" => $"UPPER({field})",
            "lower case" => $"LOWER({field})",
            "Trim" => $"TRIM({field})",
            "Length" => $"LENGTH({field})",
            "Left N Chars" => $"SUBSTR({field}, 1, {p})",
            "Right N Chars" => $"SUBSTR({field}, -({p}))",
            "Year Part" => Part("%Y", field),
            "Month Part" => Part("%m", field),
            "Day Part" => Part("%d", field),
            "Hour Part" => Part("%H", field),
            "Month Name" =>
                $"CASE STRFTIME('%m', {field}) WHEN '01' THEN 'January' WHEN '02' THEN 'February' " +
                "WHEN '03' THEN 'March' WHEN '04' THEN 'April' WHEN '05' THEN 'May' WHEN '06' THEN 'June' " +
                "WHEN '07' THEN 'July' WHEN '08' THEN 'August' WHEN '09' THEN 'September' " +
                "WHEN '10' THEN 'October' WHEN '11' THEN 'November' WHEN '12' THEN 'December' END",
            "Day Name" =>
                $"CASE STRFTIME('%w', {field}) WHEN '0' THEN 'Sunday' WHEN '1' THEN 'Monday' " +
                "WHEN '2' THEN 'Tuesday' WHEN '3' THEN 'Wednesday' WHEN '4' THEN 'Thursday' " +
                "WHEN '5' THEN 'Friday' WHEN '6' THEN 'Saturday' END",
            "Date Only" => $"DATE({field})",
            "DATEDIFF (Days)" => $"CAST(JULIANDAY(DATE({p})) - JULIANDAY(DATE({field})) AS INTEGER)",
            "DATEDIFF (Months)" =>
                $"(({Part("%Y", p)} - {Part("%Y", field)}) * 12 + {Part("%m", p)} - {Part("%m", field)})",
            "DATEDIFF (Years)" => $"({Part("%Y", p)} - {Part("%Y", field)})",
            "DATEADD (Days)" => $"DATETIME({field}, ({p}) || ' days')",
            "DATEADD (Months)" => $"DATETIME({field}, ({p}) || ' months')",
            "DATEADD (Years)" => $"DATETIME({field}, ({p}) || ' years')",
            "ISNULL (Default)" => $"IFNULL({field}, '{p}')",
            "COALESCE (Fallback)" => $"COALESCE({field}, {p})",
            "Absolute Value" => $"ABS({field})",
            "Round to N Decimals" => $"ROUND({field}, {p})",
            "Ceiling" => $"CEILING({field})",
            "Floor" => $"FLOOR({field})",
            _ => field
        };
    }
}
