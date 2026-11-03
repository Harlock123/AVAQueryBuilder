using System.Data.Common;
using AVAQueryBuilder;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace AVAQueryBuilder.Tests;

/// <summary>
/// One small shop database per engine, made fresh for the test run and dropped after it: customers, their orders
/// and a view. SQLite always runs, in a temporary file. SQL Server and PostgreSQL use the servers named by
/// AQB_TEST_SQLSERVER and AQB_TEST_POSTGRES, falling back to CCReimagined's sample servers on localhost; when
/// neither answers, their tests are skipped.
/// </summary>
public sealed class TestDatabases : IDisposable
{
    private const string DefaultSqlServer =
        "Server=localhost,1433;User ID=sa;Password=ccr_Dev_Password1;TrustServerCertificate=True;Connect Timeout=3";

    private const string DefaultPostgres =
        "Host=localhost;Username=ccr;Password=ccr_dev_password;Database=ccrsample;Timeout=3";

    private readonly Dictionary<DbEngine, TestDatabase> _made = new();
    private readonly Lock _gate = new();

    /// <summary>The engine's shop database, or a skip when its server can't be reached.</summary>
    public TestDatabase For(DbEngine engine)
    {
        TestDatabase db;
        lock (_gate)
        {
            if (!_made.TryGetValue(engine, out db!))
                _made[engine] = db = Make(engine);
        }
        if (db.SkipReason != null)
            Assert.Skip(db.SkipReason);
        return db;
    }

    private static TestDatabase Make(DbEngine engine)
    {
        try
        {
            return engine switch
            {
                DbEngine.Sqlite => MakeSqlite(),
                DbEngine.PostgreSql => MakePostgres(),
                _ => MakeSqlServer()
            };
        }
        catch (Exception ex) when (engine != DbEngine.Sqlite)
        {
            return new TestDatabase(engine, "", "", () => { }) { SkipReason = $"{Db.DisplayName(engine)} not reachable: {ex.Message}" };
        }
    }

    private static TestDatabase MakeSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aqb-test-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Pooling=False";
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            conn.Open();
            Execute(conn, """
                CREATE TABLE customer (id INTEGER PRIMARY KEY, name TEXT, nickname TEXT, code TEXT, city TEXT,
                                       joined TEXT, placed TEXT, balance NUMERIC, ratio REAL);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, amount NUMERIC);
                CREATE VIEW big_orders AS SELECT * FROM orders WHERE amount > 50;
                """);
            Seed(conn, "main");
        }
        return new TestDatabase(DbEngine.Sqlite, cs, "main", () => File.Delete(path));
    }

    private static TestDatabase MakePostgres()
    {
        var cs = Environment.GetEnvironmentVariable("AQB_TEST_POSTGRES") ?? DefaultPostgres;
        var schema = $"aqb_{Guid.NewGuid():N}"[..16];
        using (var conn = new NpgsqlConnection(cs))
        {
            conn.Open();
            Execute(conn, $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.customer (id INTEGER PRIMARY KEY, name VARCHAR(40), nickname VARCHAR(40),
                    code VARCHAR(10), city VARCHAR(40), joined DATE, placed TIMESTAMP, balance NUMERIC(10,2),
                    ratio NUMERIC(10,5));
                CREATE TABLE {schema}.orders (id INTEGER PRIMARY KEY, customer_id INTEGER, amount NUMERIC(10,2));
                CREATE VIEW {schema}.big_orders AS SELECT * FROM {schema}.orders WHERE amount > 50;
                """);
            Seed(conn, schema);
        }
        return new TestDatabase(DbEngine.PostgreSql, cs, schema, () =>
        {
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            Execute(conn, $"DROP SCHEMA {schema} CASCADE");
        });
    }

    private static TestDatabase MakeSqlServer()
    {
        var server = Environment.GetEnvironmentVariable("AQB_TEST_SQLSERVER") ?? DefaultSqlServer;
        var name = $"aqb_test_{Guid.NewGuid():N}"[..17];
        using (var conn = new SqlConnection(server))
        {
            conn.Open();
            Execute(conn, $"CREATE DATABASE {name}");
        }
        var cs = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
        using (var conn = new SqlConnection(cs))
        {
            conn.Open();
            Execute(conn, "CREATE SCHEMA shop");
            Execute(conn, """
                CREATE TABLE shop.customer (id INT PRIMARY KEY, name NVARCHAR(40), nickname NVARCHAR(40) NULL,
                    code NVARCHAR(10), city NVARCHAR(40), joined DATE, placed DATETIME2, balance DECIMAL(10,2),
                    ratio DECIMAL(10,5));
                CREATE TABLE shop.orders (id INT PRIMARY KEY, customer_id INT, amount DECIMAL(10,2));
                """);
            Execute(conn, "CREATE VIEW shop.big_orders AS SELECT * FROM shop.orders WHERE amount > 50");
            Seed(conn, "shop");
        }
        return new TestDatabase(DbEngine.SqlServer, cs, "shop", () =>
        {
            SqlConnection.ClearAllPools();
            using var conn = new SqlConnection(server);
            conn.Open();
            Execute(conn, $"ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name}");
        });
    }

    private static void Seed(DbConnection conn, string schema) => Execute(conn, $"""
        INSERT INTO {schema}.customer VALUES
            (1, 'Ada Lovelace', NULL, '  ab  ', 'London', '2024-03-15', '2024-03-15 13:45:00', -12.50, 3.14159),
            (2, 'Alan Turing', 'Prof', 'cd', 'Manchester', '2023-06-23', '2023-06-23 09:00:00', 40.00, 2.5),
            (3, 'Grace Hopper', 'Amazing Grace', 'ef', 'New York', '2022-12-09', '2022-12-09 18:30:00', 100.00, 1.25);
        INSERT INTO {schema}.orders VALUES (1, 1, 30), (2, 1, 70), (3, 2, 20), (4, 3, 90), (5, 3, 15);
        """);

    private static void Execute(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var db in _made.Values)
            db.Drop();
    }
}

public sealed record TestDatabase(DbEngine Engine, string ConnectionString, string Schema, Action Drop)
{
    public string? SkipReason { get; init; }

    /// <summary>A table's name the way the dialogs give it: "schema.table".</summary>
    public string Table(string name) => $"{Schema}.{name}";
}
