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
