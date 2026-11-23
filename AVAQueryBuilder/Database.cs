using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace AVAQueryBuilder;

/// <summary>The database engines the query builder can connect to and write SQL for.</summary>
public enum DbEngine
{
    SqlServer,
    PostgreSql,
    Sqlite
}

/// <summary>A column as the catalogue describes it.</summary>
public record ColumnDetail(string Name, string Type, bool Nullable, bool PrimaryKey);

/// <summary>A foreign key column: <c>Table.Column</c> refers to <c>RefTable.RefColumn</c> (tables as "schema.name").</summary>
public record ForeignKey(string Table, string Column, string RefTable, string RefColumn);

/// <summary>What a read-only run gave back: the columns, the first rows kept, and how many rows there were.</summary>
/// <param name="More">True when counting stopped at the limit: there are at least <paramref name="Count"/> rows.</param>
public record QueryRows(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, int Count, bool More);

/// <summary>A table or view, named the way the dialogs and the SQL use it: "schema.name".</summary>
public record TableInfo(string Schema, string Name, bool IsView)
{
    public string FullName => $"{Schema}.{Name}";
}

/// <summary>
/// Everything that talks to a database: opening a connection, reading the schema and running a query. This is
/// the only place that knows which ADO.NET provider goes with which engine.
/// </summary>
public static class Db
{
    public const int QueryTimeoutSeconds = 30;

    public static string DisplayName(DbEngine engine) => engine switch
    {
        DbEngine.PostgreSql => "PostgreSQL",
        DbEngine.Sqlite => "SQLite",
        _ => "SQL Server"
    };

    public static DbConnection Open(DbEngine engine, string connectionString)
    {
        DbConnection conn = engine switch
        {
            DbEngine.PostgreSql => new NpgsqlConnection(connectionString),
            DbEngine.Sqlite => new SqliteConnection(connectionString),
            _ => new SqlConnection(connectionString)
        };
        try
        {
            conn.Open();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Every user table and view, tables first, sorted by schema and name.</summary>
    public static List<TableInfo> ListTables(DbEngine engine, string connectionString)
    {
        var sql = engine switch
        {
            DbEngine.Sqlite =>
                "SELECT 'main', name, type = 'view' FROM sqlite_master " +
                "WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name",
            DbEngine.PostgreSql =>
                "SELECT table_schema, table_name, table_type = 'VIEW' FROM information_schema.tables " +
                "WHERE table_schema NOT IN ('pg_catalog', 'information_schema') " +
                "AND table_type IN ('BASE TABLE', 'VIEW') ORDER BY table_type, table_schema, table_name",
            _ =>
                "SELECT TABLE_SCHEMA, TABLE_NAME, CAST(CASE WHEN TABLE_TYPE = 'VIEW' THEN 1 ELSE 0 END AS BIT) " +
                "FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_TYPE, TABLE_SCHEMA, TABLE_NAME"
        };

        var tables = new List<TableInfo>();
        using var conn = Open(engine, connectionString);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1), Convert.ToBoolean(reader.GetValue(2))));
        return tables;
    }

    /// <summary>The columns of "schema.table", in table order. Empty when the name has no schema part.</summary>
    public static List<string> ListColumns(DbEngine engine, string connectionString, string schemaAndTable)
    {
        var columns = new List<string>();
        var parts = schemaAndTable.Split('.', 2);
        if (parts.Length != 2) return columns;

        using var conn = Open(engine, connectionString);
        using var cmd = conn.CreateCommand();
        if (engine == DbEngine.Sqlite)
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info($table, $schema) ORDER BY cid";
            AddParameter(cmd, "$schema", parts[0]);
            AddParameter(cmd, "$table", parts[1]);
        }
        else
        {
            cmd.CommandText =
                "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION";
            AddParameter(cmd, "@schema", parts[0]);
            AddParameter(cmd, "@table", parts[1]);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>
    /// The databases on the server the connection string points at. SQL Server asks master and PostgreSQL the
    /// postgres database (falling back to the one named); SQLite has none, the file is the database.
    /// </summary>
    public static List<string> ListDatabases(DbEngine engine, string connectionString)
    {
        var list = new List<string>();
        switch (engine)
        {
            case DbEngine.SqlServer:
            {
                var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", ConnectTimeout = 10 };
                using var conn = Open(engine, builder.ConnectionString);
                ReadNames(conn, "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", list);
                break;
            }
            case DbEngine.PostgreSql:
            {
                const string sql = "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname";
                var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 10 };
                var named = builder.Database;
                builder.Database = "postgres";
                try
                {
                    using var conn = Open(engine, builder.ConnectionString);
                    ReadNames(conn, sql, list);
                }
                catch (PostgresException) when (!string.IsNullOrWhiteSpace(named))
                {
                    // No access to the postgres database: ask the one the user named instead.
                    builder.Database = named;
                    using var conn = Open(engine, builder.ConnectionString);
                    ReadNames(conn, sql, list);
                }
                break;
            }
        }
        return list;
    }

    /// <summary>Opens and closes a connection, throwing whatever the provider throws.</summary>
    public static void Test(DbEngine engine, string connectionString)
    {
        using var conn = Open(engine, WithTimeout(engine, connectionString, 10));
    }

    /// <summary>
    /// Runs a query and returns its rows as text, the way the results grid shows them. Every column is a string
    /// (NULL becomes empty), and repeated column names get a number so none is lost.
    /// </summary>
    public static DataTable Run(DbEngine engine, string connectionString, string sql)
    {
        using var conn = Open(engine, connectionString);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        using var reader = cmd.ExecuteReader();

        var table = new DataTable();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);
            var unique = name;
            for (var n = 2; table.Columns.Contains(unique); n++)
                unique = $"{name}_{n}";
            table.Columns.Add(unique, typeof(string));
        }

        var values = new object[reader.FieldCount];
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i)) ?? string.Empty;
            table.Rows.Add(values);
        }
        return table;
    }

    /// <summary>An engine from its name as people write it: "SQL Server", "mssql", "postgres", "sqlite"…</summary>
    public static DbEngine ParseEngine(string? name)
    {
        var n = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return n switch
        {
            "sqlserver" or "mssql" or "tsql" or "microsoftsqlserver" or "azuresql" => DbEngine.SqlServer,
            "postgresql" or "postgres" or "pg" or "npgsql" => DbEngine.PostgreSql,
            "sqlite" or "sqlite3" => DbEngine.Sqlite,
            _ => throw new ArgumentException($"\"{name}\" isn't an engine this program knows: SQL Server, PostgreSQL or SQLite.")
        };
    }

    /// <summary>The columns of "schema.table": name, type, whether NULL is allowed, and the primary key. Catalogue only.</summary>
    public static List<ColumnDetail> DescribeColumns(DbEngine engine, string connectionString, string schemaAndTable)
    {
        var (schema, table) = Split(schemaAndTable);
        var list = new List<ColumnDetail>();
        using var conn = Open(engine, connectionString);
        using var cmd = conn.CreateCommand();
        if (engine == DbEngine.Sqlite)
        {
            cmd.CommandText = "SELECT name, type, \"notnull\", pk FROM pragma_table_info($table, $schema) ORDER BY cid";
            AddParameter(cmd, "$schema", schema);
            AddParameter(cmd, "$table", table);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ColumnDetail(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), Convert.ToInt64(r.GetValue(2)) == 0, Convert.ToInt64(r.GetValue(3)) > 0));
            return list;
        }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cmd.CommandText =
            "SELECT kcu.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " +
            "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME " +
            "AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA AND kcu.TABLE_NAME = tc.TABLE_NAME " +
            "WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @table";
        AddParameter(cmd, "@schema", schema);
        AddParameter(cmd, "@table", table);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) keys.Add(r.GetString(0));
        cmd.CommandText =
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var type = r.GetString(1);
                if (!r.IsDBNull(2) && Convert.ToInt64(r.GetValue(2)) is var len)
                    type += len < 0 ? "(max)" : $"({len})";
                list.Add(new ColumnDetail(r.GetString(0), type, string.Equals(r.GetString(3), "YES", StringComparison.OrdinalIgnoreCase), keys.Contains(r.GetString(0))));
            }
        return list;
    }

    /// <summary>
    /// The foreign keys that start at "schema.table" and those that point at it, from the catalogue: the joins the
    /// database itself knows about. Empty when the engine or the user's rights don't show them.
    /// </summary>
    public static List<ForeignKey> ForeignKeys(DbEngine engine, string connectionString, string schemaAndTable)
    {
        var (schema, table) = Split(schemaAndTable);
        var list = new List<ForeignKey>();
        using var conn = Open(engine, connectionString);
        if (engine == DbEngine.Sqlite)
        {
            // SQLite keeps each table's keys on the table: read them all and keep the ones that touch this table.
            var tables = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            foreach (var t in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT \"table\", \"from\", \"to\" FROM pragma_foreign_key_list($t)";
                AddParameter(cmd, "$t", t);
                var found = new List<(string Ref, string From, string? To)>();
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) found.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
                foreach (var (refTable, from, to) in found)
                {
                    if (!t.Equals(table, StringComparison.OrdinalIgnoreCase) && !refTable.Equals(table, StringComparison.OrdinalIgnoreCase)) continue;
                    // A key without a column names the referenced table's primary key.
                    var target = to ?? DescribeColumns(engine, connectionString, $"main.{refTable}").FirstOrDefault(c => c.PrimaryKey)?.Name ?? "";
                    list.Add(new ForeignKey($"main.{t}", from, $"main.{refTable}", target));
                }
            }
            return list;
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = engine == DbEngine.SqlServer
                // SQL Server's INFORMATION_SCHEMA has no POSITION_IN_UNIQUE_CONSTRAINT: its own catalogue pairs the columns.
                ? "SELECT SCHEMA_NAME(pt.schema_id), pt.name, pc.name, SCHEMA_NAME(rt.schema_id), rt.name, rc.name " +
                  "FROM sys.foreign_key_columns fkc " +
                  "JOIN sys.tables pt ON pt.object_id = fkc.parent_object_id " +
                  "JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id " +
                  "JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id " +
                  "JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id " +
                  "WHERE (SCHEMA_NAME(pt.schema_id) = @schema AND pt.name = @table) OR (SCHEMA_NAME(rt.schema_id) = @schema AND rt.name = @table)"
                : "SELECT kcu.TABLE_SCHEMA, kcu.TABLE_NAME, kcu.COLUMN_NAME, ref.TABLE_SCHEMA, ref.TABLE_NAME, ref.COLUMN_NAME " +
                "FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc " +
                "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu ON kcu.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA AND kcu.CONSTRAINT_NAME = rc.CONSTRAINT_NAME " +
                "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ref ON ref.CONSTRAINT_SCHEMA = rc.UNIQUE_CONSTRAINT_SCHEMA " +
                "AND ref.CONSTRAINT_NAME = rc.UNIQUE_CONSTRAINT_NAME AND ref.ORDINAL_POSITION = kcu.POSITION_IN_UNIQUE_CONSTRAINT " +
                "WHERE (kcu.TABLE_SCHEMA = @schema AND kcu.TABLE_NAME = @table) OR (ref.TABLE_SCHEMA = @schema AND ref.TABLE_NAME = @table)";
            AddParameter(cmd, "@schema", schema);
            AddParameter(cmd, "@table", table);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ForeignKey($"{r.GetString(0)}.{r.GetString(1)}", r.GetString(2), $"{r.GetString(3)}.{r.GetString(4)}", r.GetString(5)));
        }
        return list;
    }

    /// <summary>
    /// Columns whose name contains <paramref name="text"/> (case-insensitive), in every table and view: (table as
    /// "schema.name", column, type). Catalogue only; at most <paramref name="limit"/>.
    /// </summary>
    public static List<(string Table, string Column, string Type)> FindColumns(DbEngine engine, string connectionString, string text, int limit = 200)
    {
        var found = new List<(string, string, string)>();
        var like = "%" + text.Trim().ToLowerInvariant() + "%";
        if (engine == DbEngine.Sqlite)
        {
            foreach (var t in ListTables(engine, connectionString))
            {
                foreach (var c in DescribeColumns(engine, connectionString, t.FullName))
                    if (c.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) || t.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add((t.FullName, c.Name, c.Type));
                        if (found.Count >= limit) return found;
                    }
            }
            return found;
        }
        using var conn = Open(engine, connectionString);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS c " +
            "JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME " +
            "WHERE (LOWER(c.COLUMN_NAME) LIKE @p OR LOWER(c.TABLE_NAME) LIKE @p) " +
            (engine == DbEngine.PostgreSql ? "AND c.TABLE_SCHEMA NOT IN ('pg_catalog', 'information_schema') " : "") +
            "ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION";
        AddParameter(cmd, "@p", like);
        using var r = cmd.ExecuteReader();
        while (r.Read() && found.Count < limit)
            found.Add(($"{r.GetString(0)}.{r.GetString(1)}", r.GetString(2), r.GetString(3)));
        return found;
    }

    /// <summary>
    /// Runs a query inside a transaction that is always rolled back (and read-only where the engine has such a thing),
    /// so whatever the SQL says, nothing in the database changes. Keeps the first <paramref name="keep"/> rows as text
    /// and counts rows up to <paramref name="countUpTo"/>.
    /// </summary>
    public static QueryRows RunReadOnly(DbEngine engine, string connectionString, string sql, int keep, int countUpTo = 100_000)
    {
        if (engine == DbEngine.Sqlite)
            connectionString = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ConnectionString;
        using var conn = Open(engine, connectionString);
        using var tx = conn.BeginTransaction();
        try
        {
            if (engine == DbEngine.PostgreSql)
            {
                using var ro = conn.CreateCommand();
                ro.Transaction = tx;
                ro.CommandText = "SET TRANSACTION READ ONLY";
                ro.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.CommandTimeout = QueryTimeoutSeconds;
            var columns = new List<string>();
            var rows = new List<string[]>();
            var count = 0;
            var more = false;
            using (var reader = cmd.ExecuteReader())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    columns.Add(string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i));
                while (reader.Read())
                {
                    if (count >= countUpTo)
                    {
                        more = true;
                        break;
                    }
                    if (rows.Count < keep)
                    {
                        var values = new string[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                            values[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        rows.Add(values);
                    }
                    count++;
                }
            }
            return new QueryRows(columns, rows, count, more);
        }
        finally
        {
            try { tx.Rollback(); } catch (Exception e) when (e is InvalidOperationException or DbException) { }
        }
    }

    /// <summary>Rows as the results grid takes them: every column text, repeated names numbered.</summary>
    public static DataTable ToTable(QueryRows rows)
    {
        var table = new DataTable();
        foreach (var name in rows.Columns)
        {
            var unique = name;
            for (var n = 2; table.Columns.Contains(unique); n++)
                unique = $"{name}_{n}";
            table.Columns.Add(unique, typeof(string));
        }
        foreach (var row in rows.Rows)
            table.Rows.Add(row.Cast<object>().ToArray());
        return table;
    }

    private static (string Schema, string Table) Split(string schemaAndTable)
    {
        var parts = schemaAndTable.Split('.', 2);
        if (parts.Length != 2) throw new ArgumentException($"\"{schemaAndTable}\" isn't schema.table (for example dbo.Patients, public.patients or main.patients).");
        return (parts[0], parts[1]);
    }

    private static string WithTimeout(DbEngine engine, string connectionString, int seconds) => engine switch
    {
        DbEngine.SqlServer => new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = seconds }.ConnectionString,
        DbEngine.PostgreSql => new NpgsqlConnectionStringBuilder(connectionString) { Timeout = seconds }.ConnectionString,
        _ => connectionString
    };

    private static void ReadNames(DbConnection conn, string sql, List<string> list)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
