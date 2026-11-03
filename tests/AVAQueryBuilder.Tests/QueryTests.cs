using System.Data;
using System.Globalization;
using System.Text.Json;
using AVAQueryBuilder;

namespace AVAQueryBuilder.Tests;

/// <summary>
/// Builds queries the way the dialogs do (fields named "schema.table.column", lookups as LOOKUP_n) and runs them
/// on each engine, so the SQL each dialect writes is checked by the database itself, not by eye.
/// </summary>
public sealed class QueryTests(TestDatabases databases) : IClassFixture<TestDatabases>
{
    public static TheoryData<DbEngine> Engines => new(DbEngine.SqlServer, DbEngine.PostgreSql, DbEngine.Sqlite);

    private static TableSourceResult Customers(TestDatabase db, params string[] columns) => new()
    {
        TableName = db.Table("customer"),
        SelectedColumns = columns.ToList()
    };

    private static ConnectedSourceResult Orders(TestDatabase db, string joinType = "INNER JOIN") => new()
    {
        SourceTableName = db.Table("customer"),
        JoinFieldsFromSource = ["id"],
        LookupTableName = db.Table("orders"),
        JoinFieldInLookup = "customer_id",
        JoinType = joinType,
        ReturnFields = ["amount"],
        ColumnAliases = { ["amount"] = "Order Amount" },
        OrdinalValue = 1
    };

    private static DataTable Run(TestDatabase db, params object[] entities)
    {
        var sql = QueryBuilder.BuildQuery(entities, db.Engine);
        try
        {
            return Db.Run(db.Engine, db.ConnectionString, sql);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{Db.DisplayName(db.Engine)} rejected:\n{sql}\n{ex.Message}");
        }
    }

    private static string[] Column(DataTable table, string name) =>
        table.Rows.Cast<DataRow>().Select(r => (string)r[name]).ToArray();

    private static decimal Number(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.CurrentCulture);

    [Theory, MemberData(nameof(Engines))]
    public void Lists_tables_views_and_columns(DbEngine engine)
    {
        var db = databases.For(engine);
        var tables = Db.ListTables(engine, db.ConnectionString).Where(t => t.Schema == db.Schema).ToList();

        Assert.Equal(["customer", "orders"], tables.Where(t => !t.IsView).Select(t => t.Name).Order());
        Assert.Equal(["big_orders"], tables.Where(t => t.IsView).Select(t => t.Name));
        Assert.Equal(
            ["id", "name", "nickname", "code", "city", "joined", "placed", "balance", "ratio"],
            Db.ListColumns(engine, db.ConnectionString, db.Table("customer")));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Joins_aliases_sorts_and_limits(DbEngine engine)
    {
        var db = databases.For(engine);
        var customers = Customers(db, "id", "name");
        customers.ColumnAliases["name"] = "Customer Name";

        var rows = Run(db, customers, Orders(db),
            new SortingResult { Fields = [new SortingField { Field = "LOOKUP_1.amount", Direction = "DESC" }] },
            new LimiterResult { TopCount = 2 });

        Assert.Equal(["Grace Hopper", "Ada Lovelace"], Column(rows, "Customer Name"));
        Assert.Equal([90m, 70m], Column(rows, "Order Amount").Select(Number));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Groups_with_aggregates_and_having(DbEngine engine)
    {
        var db = databases.For(engine);
        var name = $"{db.Table("customer")}.name";

        var rows = Run(db, Customers(db), Orders(db),
            new GroupByResult
            {
                GroupFields = [name],
                Aggregates =
                [
                    new AggregateField { Function = "SUM", Field = "LOOKUP_1.amount", Alias = "Total" },
                    new AggregateField { Function = "COUNT(*)", Alias = "Orders" }
                ],
                HavingConditions = [new HavingCondition { Expression = "SUM(LOOKUP_1.amount)", Operator = ">", Value = "50" }]
            },
            new SortingResult { Fields = [new SortingField { Field = name }] });

        Assert.Equal(["Ada Lovelace", "Grace Hopper"], Column(rows, "name"));
        Assert.Equal([100m, 105m], Column(rows, "Total").Select(Number));
        Assert.Equal(["2", "2"], Column(rows, "Orders"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Filters_with_casts_and_combiners(DbEngine engine)
    {
        var db = databases.For(engine);
        var t = db.Table("customer");

        var rows = Run(db, Customers(db, "id"),
            new FilterResult
            {
                Conditions =
                [
                    new FilterCondition { Field = $"{t}.city", Operator = "IN", Value = "London, New York" },
                    new FilterCondition { Field = $"{t}.joined", CastAs = "DATE", Operator = "BETWEEN", Value = "2022-01-01", Value2 = "2023-12-31" },
                    new FilterCondition { Field = $"{t}.name", Operator = "LIKE", Value = "G%" }
                ]
            },
            new FilterResult
            {
                Combiner = "OR",
                Conditions =
                [
                    new FilterCondition { Field = $"{t}.balance", CastAs = "DECIMAL", Operator = ">=", Value = "100" },
                    new FilterCondition { Field = $"{t}.nickname", Operator = "IS NULL" }
                ]
            });

        Assert.Equal(["3"], Column(rows, "id"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Distinct_and_case_when(DbEngine engine)
    {
        var db = databases.For(engine);
        var orders = new TableSourceResult { TableName = db.Table("orders"), SelectedColumns = ["customer_id"] };

        var rows = Run(db, orders, new DistinctResult(),
            new CaseWhenResult
            {
                Expressions =
                [
                    new CaseExpression
                    {
                        Field = "customer_id",
                        Conditions = [new WhenThen { Operator = "=", WhenValue = "1", ThenValue = "first" }],
                        ElseValue = "later",
                        Alias = "Which"
                    }
                ]
            },
            new SortingResult { Fields = [new SortingField { Field = "customer_id" }] });

        Assert.Equal(["1", "2", "3"], Column(rows, "customer_id"));
        Assert.Equal(["first", "later", "later"], Column(rows, "Which"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Field_browser_preview_limits_rows(DbEngine engine)
    {
        var db = databases.For(engine);
        var rows = Db.Run(engine, db.ConnectionString, Dialect.Preview(engine, db.Table("orders"), 2));
        Assert.Equal(2, rows.Rows.Count);
    }

    /// <summary>Every derivation, on Ada Lovelace's row (joined 2024-03-15, a Friday; placed at 13:45).</summary>
    public static TheoryData<DbEngine, string, string, string, string> Derivations()
    {
        var cases = new (string Derivation, string Field, string Parameter, string Expected)[]
        {
            ("UPPER CASE", "name", "", "ADA LOVELACE"),
            ("lower case", "name", "", "ada lovelace"),
            ("Trim", "code", "", "ab"),
            ("Length", "name", "", "12"),
            ("Left N Chars", "name", "3", "Ada"),
            ("Right N Chars", "name", "4", "lace"),
            ("Reverse", "name", "", "ecalevoL adA"),
            ("Year Part", "joined", "", "2024"),
            ("Month Part", "joined", "", "3"),
            ("Day Part", "joined", "", "15"),
            ("Hour Part", "placed", "", "13"),
            ("Month Name", "joined", "", "March"),
            ("Day Name", "joined", "", "Friday"),
            ("Date Only", "placed", "", "date:2024-03-15"),
            ("DATEDIFF (Days)", "joined", "'2024-04-01'", "17"),
            ("DATEDIFF (Months)", "joined", "'2024-04-01'", "1"),
            ("DATEDIFF (Years)", "joined", "'2026-01-01'", "2"),
            ("DATEADD (Days)", "joined", "10", "date:2024-03-25"),
            ("DATEADD (Months)", "joined", "2", "date:2024-05-15"),
            ("DATEADD (Years)", "joined", "1", "date:2025-03-15"),
            ("ISNULL (Default)", "nickname", "none", "none"),
            ("COALESCE (Fallback)", "nickname", "'unknown'", "unknown"),
            ("Absolute Value", "balance", "", "number:12.5"),
            ("Round to N Decimals", "ratio", "2", "number:3.14"),
            ("Ceiling", "ratio", "", "number:4"),
            ("Floor", "ratio", "", "number:3"),
        };

        var data = new TheoryData<DbEngine, string, string, string, string>();
        foreach (var engine in new[] { DbEngine.SqlServer, DbEngine.PostgreSql, DbEngine.Sqlite })
            foreach (var c in cases)
                if (Dialect.Derivations(engine).Contains(c.Derivation))
                    data.Add(engine, c.Derivation, c.Field, c.Parameter, c.Expected);
        return data;
    }

    [Theory, MemberData(nameof(Derivations))]
    public void Derivation_runs_and_gives_the_right_value(DbEngine engine, string derivation, string field, string parameter, string expected)
    {
        var db = databases.For(engine);
        var t = db.Table("customer");

        var rows = Run(db, Customers(db, "id"),
            new DerivedFieldResult
            {
                Fields = [new DerivedField { SourceField = $"{t}.{field}", Derivation = derivation, Parameter = parameter, Alias = "v" }]
            },
            new FilterResult { Conditions = [new FilterCondition { Field = $"{t}.id", Operator = "=", Value = "1" }] });

        var value = Assert.Single(Column(rows, "v"));
        if (expected.StartsWith("date:"))
            Assert.Equal(DateTime.Parse(expected[5..], CultureInfo.InvariantCulture), DateTime.Parse(value, CultureInfo.CurrentCulture).Date);
        else if (expected.StartsWith("number:"))
            Assert.Equal(decimal.Parse(expected[7..], CultureInfo.InvariantCulture), Number(value));
        else
            Assert.Equal(expected, value);
    }

    [Fact]
    public void Sql_server_keeps_top_and_brackets_and_the_others_use_limit_and_quotes()
    {
        object[] entities =
        [
            new TableSourceResult { TableName = "dbo.customer", SelectedColumns = ["name"], ColumnAliases = { ["name"] = "Who" } },
            new LimiterResult { TopCount = 5 }
        ];

        Assert.Equal("SELECT TOP 5 name AS [Who]\nFROM dbo.customer", QueryBuilder.BuildQuery(entities, DbEngine.SqlServer).ReplaceLineEndings("\n"));
        Assert.Equal("SELECT name AS \"Who\"\nFROM dbo.customer\nLIMIT 5", QueryBuilder.BuildQuery(entities, DbEngine.PostgreSql).ReplaceLineEndings("\n"));
        Assert.Equal("SELECT name AS \"Who\"\nFROM dbo.customer\nLIMIT 5", QueryBuilder.BuildQuery(entities, DbEngine.Sqlite).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Query_files_from_before_engines_load_as_sql_server()
    {
        var old = JsonSerializer.Deserialize<QueryFile>("""{"ConnectionString":"Server=x","LookupOrdinal":0,"Entities":[],"Connectors":[]}""")!;
        Assert.Equal(DbEngine.SqlServer, old.Engine);

        var json = JsonSerializer.Serialize(new QueryFile { Engine = DbEngine.PostgreSql });
        Assert.Contains("\"Engine\":\"PostgreSql\"", json);
        Assert.Equal(DbEngine.PostgreSql, JsonSerializer.Deserialize<QueryFile>(json)!.Engine);
    }
}
