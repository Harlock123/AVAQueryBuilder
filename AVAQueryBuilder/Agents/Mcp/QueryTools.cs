using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>A condition in a WHERE filter.</summary>
public sealed class ConditionSpec
{
    [JsonPropertyName("field"), Description("The field: \"patients.last_name\", \"last_name\", or a lookup's \"LOOKUP_1.name\" (get_query lists the query's fields).")]
    public string Field { get; set; } = "";

    [JsonPropertyName("op"), Description("=, !=, <, >, <=, >=, LIKE, NOT LIKE, IN, NOT IN, BETWEEN, IS NULL, IS NOT NULL, IS EMPTY or IS NOT EMPTY. Default =.")]
    public string? Op { get; set; }

    [JsonPropertyName("value"), Description("The value, as text (it is quoted for you; LIKE takes % and _). For IN, values separated by commas. Not needed for IS NULL and the like.")]
    public string? Value { get; set; }

    [JsonPropertyName("values"), Description("For IN and NOT IN: the values as a list, instead of value.")]
    public List<string>? Values { get; set; }

    [JsonPropertyName("value2"), Description("BETWEEN's upper value.")]
    public string? Value2 { get; set; }

    [JsonPropertyName("cast"), Description("Compare the field as VARCHAR, INT, DATE, DATETIME or DECIMAL. Optional.")]
    public string? Cast { get; set; }
}

/// <summary>A lookup: a join from the base table to another table.</summary>
public sealed class LookupSpec
{
    [JsonPropertyName("table"), Description("The table to look up in: \"providers\" or \"dbo.Providers\".")]
    public string Table { get; set; } = "";

    [JsonPropertyName("on"), Description("Its column the base table's columns match (usually its key): \"provider_id\".")]
    public string On { get; set; } = "";

    [JsonPropertyName("from"), Description("The base table's column (or columns, all matched to on) that point at it: [\"provider_id\"].")]
    public List<string> From { get; set; } = [];

    [JsonPropertyName("columns"), Description("Its columns to return: [\"name\", \"specialty\"].")]
    public List<string> Columns { get; set; } = [];

    [JsonPropertyName("aliases"), Description("Column headings for the returned columns: {\"name\": \"Provider\"}. Optional.")]
    public Dictionary<string, string>? Aliases { get; set; }

    [JsonPropertyName("join"), Description("LEFT (keep every base row, the default), INNER (only rows with a match), RIGHT or FULL.")]
    public string? Join { get; set; }
}

/// <summary>A derived field: a function applied to a field.</summary>
public sealed class DerivedSpec
{
    [JsonPropertyName("field"), Description("The field it's made from.")]
    public string Field { get; set; } = "";

    [JsonPropertyName("derivation"), Description("One of list_options' derivations: \"Year Part\", \"Month Name\", \"UPPER CASE\", \"DATEDIFF (Years)\", \"Round to N Decimals\"…")]
    public string Derivation { get; set; } = "";

    [JsonPropertyName("parameter"), Description("For the derivations that take one (list_options says which): a number, a field, or a date expression in the database's own SQL (GETDATE(), NOW(), DATE('now')).")]
    public string? Parameter { get; set; }

    [JsonPropertyName("alias"), Description("Its column heading, by which group_by and sort can name it: \"Year Joined\".")]
    public string? Alias { get; set; }
}

/// <summary>One WHEN … THEN … of a CASE expression.</summary>
public sealed class WhenSpec
{
    [JsonPropertyName("op"), Description("=, !=, <, >, <=, >=, LIKE, IS NULL or IS NOT NULL. Default =.")]
    public string? Op { get; set; }

    [JsonPropertyName("when"), Description("The value compared with (quoted for you).")]
    public string? When { get; set; }

    [JsonPropertyName("then"), Description("The result when it matches.")]
    public string Then { get; set; } = "";
}

/// <summary>A CASE expression: a field's values turned into labels.</summary>
public sealed class CaseSpec
{
    [JsonPropertyName("field"), Description("The field tested.")]
    public string Field { get; set; } = "";

    [JsonPropertyName("cases"), Description("The WHEN … THEN … pairs, in order.")]
    public List<WhenSpec> Cases { get; set; } = [];

    [JsonPropertyName("else"), Description("The result when none match. Optional.")]
    public string? Else { get; set; }

    [JsonPropertyName("alias"), Description("Its column heading: \"Age Band\".")]
    public string? Alias { get; set; }
}

/// <summary>An aggregate in a GROUP BY.</summary>
public sealed class AggregateSpec
{
    [JsonPropertyName("function"), Description("COUNT(*), COUNT, COUNT DISTINCT, SUM, AVG, MIN or MAX.")]
    public string Function { get; set; } = "COUNT(*)";

    [JsonPropertyName("field"), Description("The field (not for COUNT(*)).")]
    public string? Field { get; set; }

    [JsonPropertyName("alias"), Description("Its column heading: \"Visits\".")]
    public string? Alias { get; set; }
}

/// <summary>A HAVING condition on an aggregate.</summary>
public sealed class HavingSpec
{
    [JsonPropertyName("aggregate"), Description("The aggregate: its alias (\"Visits\") or its SQL (\"COUNT(*)\").")]
    public string Aggregate { get; set; } = "";

    [JsonPropertyName("op"), Description("=, !=, <, >, <= or >=. Default >.")]
    public string? Op { get; set; }

    [JsonPropertyName("value"), Description("What it's compared with: a number, or text.")]
    public string Value { get; set; } = "";
}

/// <summary>A sort field.</summary>
public sealed class SortSpec
{
    [JsonPropertyName("field"), Description("A field, or a derived field's, CASE expression's or aggregate's alias.")]
    public string Field { get; set; } = "";

    [JsonPropertyName("direction"), Description("ASC (the default) or DESC.")]
    public string? Direction { get; set; }
}

/// <summary>A GROUP BY: the fields rows are grouped by, and what's counted or summed in each group.</summary>
public sealed class GroupSpec
{
    [JsonPropertyName("fields"), Description("The fields to group by: fields, or derived fields' and CASE expressions' aliases.")]
    public List<string> Fields { get; set; } = [];

    [JsonPropertyName("aggregates"), Description("What each group shows: [{\"function\": \"COUNT(*)\", \"alias\": \"Patients\"}].")]
    public List<AggregateSpec> Aggregates { get; set; } = [];

    [JsonPropertyName("having"), Description("Conditions on the aggregates: [{\"aggregate\": \"Patients\", \"op\": \">\", \"value\": \"10\"}]. Optional.")]
    public List<HavingSpec>? Having { get; set; }

    [JsonPropertyName("having_combiner"), Description("AND (the default) or OR, between the having conditions.")]
    public string? HavingCombiner { get; set; }
}

/// <summary>A whole query, for build_query.</summary>
public sealed class QuerySpec
{
    [JsonPropertyName("table"), Description("The base table everything starts from.")]
    public string Table { get; set; } = "";

    [JsonPropertyName("columns"), Description("Its columns to show. Default: all of them (none when grouping).")]
    public List<string>? Columns { get; set; }

    [JsonPropertyName("aliases"), Description("Column headings: {\"last_name\": \"Last Name\"}. Optional.")]
    public Dictionary<string, string>? Aliases { get; set; }

    [JsonPropertyName("lookups"), Description("Joins to other tables, in order: the first is LOOKUP_1, the next LOOKUP_2.")]
    public List<LookupSpec>? Lookups { get; set; }

    [JsonPropertyName("filters"), Description("WHERE conditions (they may use the lookups' fields).")]
    public List<ConditionSpec>? Filters { get; set; }

    [JsonPropertyName("combiner"), Description("AND (the default) or OR, between the filters.")]
    public string? Combiner { get; set; }

    [JsonPropertyName("derived"), Description("Derived fields.")]
    public List<DerivedSpec>? Derived { get; set; }

    [JsonPropertyName("case_when"), Description("CASE expressions.")]
    public List<CaseSpec>? CaseWhen { get; set; }

    [JsonPropertyName("group_by"), Description("Grouping, with counts or sums per group.")]
    public GroupSpec? GroupBy { get; set; }

    [JsonPropertyName("sort"), Description("The order of the rows.")]
    public List<SortSpec>? Sort { get; set; }

    [JsonPropertyName("limit"), Description("Only the first N rows. Optional.")]
    public int? Limit { get; set; }

    [JsonPropertyName("distinct"), Description("Leave out duplicate rows. Default false.")]
    public bool? Distinct { get; set; }
}

/// <summary>
/// The MCP tools: what an agent can do with AVA Query Builder. Each handler reaches the query only through
/// <see cref="IQueryHost.RunAsync{T}"/>, and reads the database (its catalogue, or a query's rows) off that thread. Every
/// change is one undo step, a call that fails leaves the query as it was, and failures come back as MCP tool errors
/// with a message that says what to do instead. Nothing here writes to a database: queries run in a transaction
/// that is rolled back.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case.</remarks>
public sealed partial class QueryTools(IQueryHost host)
{
    private readonly Workspace _files = new(host.Root);

    /// <summary>Files export_results wrote while agents may not see rows: read_file won't give them back.</summary>
    private readonly HashSet<string> _hiddenExports = new(StringComparer.Ordinal);

    public const int MaxRowsToAgent = 200;

    private const string IdHelp = "The entity's id from get_query (\"e3\").";

    // ───────────────────────────── the query ─────────────────────────────

    [McpServerTool(Name = "get_query", ReadOnly = true, Idempotent = true)]
    [Description("The open query: its file, the database it's connected to (engine and server, never the password), whether it has unsaved changes, each entity on the diagram (id, kind, label and settings), the fields it offers for filters, sorting and grouping, and its SQL. Start here.")]
    public async Task<string> GetQuery(CancellationToken cancel = default)
    {
        var (doc, engine, cs, info) = await Run("read the query", s => (s.Document, s.Engine, s.ConnectionString, Summary(s)), cancel).ConfigureAwait(false);
        info["entities"] = new JsonArray(doc.Entities.Select(e => (JsonNode)Entity(e)).ToArray());
        if (cs.Length > 0 && doc.Entities.Count > 0)
        {
            try
            {
                var catalog = await Task.Run(() => Catalog.Load(engine, cs, Tables(doc)), cancel).ConfigureAwait(false);
                var fields = Fields.All(doc, catalog);
                info["fields"] = new JsonArray(fields.Take(300).Select(f => (JsonNode)f).ToArray());
                var expressions = Expressions(doc, engine);
                if (expressions.Count > 0) info["expressions"] = expressions;
            }
            catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException or ArgumentException)
            {
                info["fields_problem"] = $"Couldn't read the tables' columns: {e.Message}";
            }
        }
        info["sql"] = QueryEdits.Sql(doc, engine);
        return info.ToJsonString(Json.Options);
    }

    [McpServerTool(Name = "get_sql", ReadOnly = true, Idempotent = true)]
    [Description("The query's SQL, as Copy SQL gives it: for the connected database's engine, or written for another engine (SQL Server, PostgreSQL or SQLite) to port it.")]
    public Task<string> GetSql(
        [Description("SQL Server, PostgreSQL or SQLite. Default: the connected database's.")] string? engine = null,
        CancellationToken cancel = default) =>
        Read("read the SQL", s =>
        {
            var e = engine is null ? s.Engine : Db.ParseEngine(engine);
            return new JsonObject { ["engine"] = Db.DisplayName(e), ["sql"] = QueryEdits.Sql(s.Document, e) };
        }, cancel);

    [McpServerTool(Name = "new_query")]
    [Description("Clears the diagram for a new query (the connection stays). Fails if the open query has unsaved changes, unless discard_changes is true.")]
    public Task<string> NewQuery(
        [Description("Throw away unsaved changes to the open query. Default false.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Read("started a new query", s =>
        {
            Discardable(s, discard_changes);
            s.Replace(new QueryFile { Engine = s.Engine }, null);
            s.Refresh();
            return Summary(s);
        }, cancel);

    [McpServerTool(Name = "open_query")]
    [Description("Opens a saved query (.qry) from the working folder, replacing the open one, and connects to the database it was saved with. Fails if the open query has unsaved changes, unless discard_changes is true.")]
    public async Task<string> OpenQuery(
        [Description("The .qry file, relative to the working folder.")] string path,
        [Description("Throw away unsaved changes to the open query. Default false.")] bool discard_changes = false,
        CancellationToken cancel = default)
    {
        var full = _files.Resolve(path, "A saved query", ".qry");
        if (!File.Exists(full)) throw new McpException($"There's no {path} in the working folder. list_files shows what is there.");
        var file = Wrap(() => QueryEdits.FromJson(File.ReadAllText(full)));
        if (file.ConnectionString.Length > 0) await Test(file.Engine, file.ConnectionString, cancel).ConfigureAwait(false);
        return await Read("opened a query", s =>
        {
            Discardable(s, discard_changes);
            if (file.ConnectionString.Length > 0) s.Connect(file.Engine, file.ConnectionString, null);
            s.Replace(file, full);
            s.Refresh();
            var info = Summary(s);
            info["entities"] = new JsonArray(s.Document.Entities.Select(e => (JsonNode)Entity(e)).ToArray());
            info["sql"] = QueryEdits.Sql(s.Document, s.Engine);
            return info;
        }, cancel).ConfigureAwait(false);
    }

    [McpServerTool(Name = "save_query")]
    [Description("Saves the query as a .qry file in the working folder (with its connection, as Save Query does), to open again later. Won't write over a file it didn't write unless overwrite is true.")]
    public Task<string> SaveQuery(
        [Description("The file, relative to the working folder (.qry is added if missing). Default: the file it was opened from or last saved to.")] string? path = null,
        [Description("Write over an existing file. Default false.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Read("saved the query", s =>
        {
            var target = path is { Length: > 0 } p ? _files.Resolve(p.EndsWith(".qry", StringComparison.OrdinalIgnoreCase) ? p : p + ".qry")
                : s.FilePath ?? throw new ArgumentException("The query has no file yet: give a path such as \"queries/active-patients.qry\".");
            _files.Resolve(Relative(target));
            if (target != s.FilePath) _files.CheckWritable(target, overwrite);
            var doc = s.Document;
            doc.ConnectionString = s.ConnectionString;
            doc.Engine = s.Engine;
            File.WriteAllText(target, QueryEdits.ToJson(doc));
            _files.Wrote(target);
            s.Saved(target);
            return new JsonObject { ["saved"] = Relative(target), ["entities"] = doc.Entities.Count, ["note"] = "A .qry file holds the connection string, with its password if it has one: keep it out of shared folders and repositories." };
        }, cancel);

    // ───────────────────────────── databases ─────────────────────────────

    [McpServerTool(Name = "list_connections", ReadOnly = true, Idempotent = true)]
    [Description("The database the query is connected to now, and the connections the person has saved (by name: engine and server, never passwords). connect switches to one.")]
    public Task<string> ListConnections(CancellationToken cancel = default) =>
        Read("listed connections", s => new JsonObject
        {
            ["connected"] = s.ConnectionString.Length == 0 ? null : Connection(s),
            ["saved"] = new JsonArray(host.Connections.All.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["engine"] = Db.DisplayName(c.Engine),
                ["connection"] = c.Redacted,
                ["note"] = c.Note,
            }).ToArray()),
        }, cancel);

    [McpServerTool(Name = "connect")]
    [Description("Connects the query to a database: a saved connection by name (list_connections), or a SQLite file in the working folder. The SQL is then written for that database's engine. The query's entities stay, so a query can be moved to a copy of the database on another engine.")]
    public async Task<string> Connect(
        [Description("A saved connection's name.")] string? name = null,
        [Description("Or a SQLite database file, relative to the working folder.")] string? sqlite_file = null,
        CancellationToken cancel = default)
    {
        if ((name is null) == (sqlite_file is null)) throw new McpException("Give name (a saved connection) or sqlite_file, one of them.");
        DbEngine engine;
        string cs;
        string? label = null;
        if (name != null)
        {
            var c = Wrap(() => host.Connections.Get(name));
            (engine, cs, label) = (c.Engine, c.ConnectionString, c.Name);
        }
        else
        {
            var full = _files.Resolve(sqlite_file!);
            if (!File.Exists(full)) throw new McpException($"There's no {sqlite_file} in the working folder.");
            (engine, cs) = (DbEngine.Sqlite, $"Data Source={full}");
        }
        await Test(engine, cs, cancel).ConfigureAwait(false);
        var tables = await Task.Run(() => Db.ListTables(engine, cs), cancel).ConfigureAwait(false);
        return await Read("connected to a database", s =>
        {
            s.Connect(engine, cs, label);
            s.Refresh();
            var info = Connection(s);
            info["tables"] = tables.Count(t => !t.IsView);
            info["views"] = tables.Count(t => t.IsView);
            info["sql"] = QueryEdits.Sql(s.Document, s.Engine);
            return info;
        }, cancel).ConfigureAwait(false);
    }

    [McpServerTool(Name = "add_connection")]
    [Description("Adds a database connection by name for this session (save keeps it for next time too), then connect uses it. Its connection string is never shown again, only the server and database.")]
    public async Task<string> AddConnection(
        [Description("A name for it: \"clinic\".")] string name,
        [Description("SQL Server, PostgreSQL or SQLite.")] string engine,
        [Description("The ADO.NET connection string: \"Server=db1;Database=Clinic;Integrated Security=true;TrustServerCertificate=true\", \"Host=db1;Database=clinic;Username=reader;Password=…\", \"Data Source=clinic.db\".")] string connection_string,
        [Description("Keep it in the person's connections for next time. Default false.")] bool save = false,
        [Description("A note on what it is. Optional.")] string? note = null,
        CancellationToken cancel = default)
    {
        var e = Wrap(() => Db.ParseEngine(engine));
        await Test(e, connection_string, cancel).ConfigureAwait(false);
        var c = new DataConnection { Name = name, Provider = e.ToString(), ConnectionString = connection_string, Note = note };
        Wrap(() => { host.Connections.Put(c, save); return true; });
        return new JsonObject { ["added"] = c.Name, ["engine"] = Db.DisplayName(e), ["connection"] = c.Redacted, ["saved"] = save }.ToJsonString(Json.Options);
    }

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true)]
    [Description("The connected database's tables and views (schema.name), optionally only those whose name contains some text. From the catalogue: no rows are read.")]
    public Task<string> ListTables(
        [Description("Only names containing this text. Optional.")] string? contains = null,
        CancellationToken cancel = default) =>
        ReadDb("listed tables", (engine, cs) =>
        {
            var all = Db.ListTables(engine, cs).Where(t => contains is null || t.FullName.Contains(contains.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return new JsonObject
            {
                ["engine"] = Db.DisplayName(engine),
                ["tables"] = new JsonArray(all.Where(t => !t.IsView).Take(500).Select(t => (JsonNode)t.FullName).ToArray()),
                ["views"] = new JsonArray(all.Where(t => t.IsView).Take(500).Select(t => (JsonNode)t.FullName).ToArray()),
                ["more"] = all.Count > 1000 ? "Over 500 of a kind: narrow it with contains." : null,
            };
        }, cancel);

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true)]
    [Description("A table's or view's columns (name, type, whether it allows NULL, the primary key), the foreign keys from it and to it, and the lookups those keys suggest, ready for add_lookup. From the catalogue: no rows are read.")]
    public Task<string> DescribeTable(
        [Description("The table: \"patients\" or \"dbo.Patients\".")] string table,
        CancellationToken cancel = default) =>
        ReadDb("read a table's columns", (engine, cs) =>
        {
            var catalog = Catalog.Load(engine, cs, [table]);
            var t = catalog.Table(table);
            var keys = Db.ForeignKeys(engine, cs, t.FullName);
            var outgoing = keys.Where(k => k.Table.Equals(t.FullName, StringComparison.OrdinalIgnoreCase)).ToList();
            var incoming = keys.Where(k => k.RefTable.Equals(t.FullName, StringComparison.OrdinalIgnoreCase) && !outgoing.Contains(k)).ToList();
            return new JsonObject
            {
                ["table"] = t.FullName,
                ["kind"] = t.IsView ? "view" : "table",
                ["columns"] = new JsonArray(catalog.Columns(t.FullName).Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name, ["type"] = c.Type, ["nullable"] = c.Nullable, ["key"] = c.PrimaryKey ? true : null,
                }).ToArray()),
                ["references"] = new JsonArray(outgoing.Select(k => (JsonNode)$"{k.Column} → {k.RefTable}.{k.RefColumn}").ToArray()),
                ["referenced_by"] = new JsonArray(incoming.Select(k => (JsonNode)$"{k.Table}.{k.Column} → {k.RefColumn}").ToArray()),
                ["lookups"] = new JsonArray(outgoing.GroupBy(k => k.RefTable).Select(g => (JsonNode)new JsonObject
                {
                    ["table"] = g.Key, ["on"] = g.First().RefColumn, ["from"] = new JsonArray(g.Select(k => (JsonNode)k.Column).Take(1).ToArray()),
                }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "find_columns", ReadOnly = true, Idempotent = true)]
    [Description("Searches every table's and view's columns (and table names) for some text: where is the date of birth kept, which tables have a provider_id. From the catalogue: no rows are read.")]
    public Task<string> FindColumns(
        [Description("Text in the column's or table's name: \"birth\", \"provider\", \"zip\".")] string text,
        CancellationToken cancel = default) =>
        ReadDb("searched the columns", (engine, cs) =>
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Give some text to look for.");
            var found = Db.FindColumns(engine, cs, text, 201);
            return new JsonObject
            {
                ["found"] = new JsonArray(found.Take(200).Select(f => (JsonNode)new JsonObject { ["table"] = f.Table, ["column"] = f.Column, ["type"] = f.Type }).ToArray()),
                ["more"] = found.Count > 200 ? "Over 200: search for something narrower." : null,
            };
        }, cancel);

    [McpServerTool(Name = "list_options", ReadOnly = true, Idempotent = true)]
    [Description("The choices the entities take, for the connected engine: derivations (and which need a parameter), filter operators and casts, CASE operators, aggregate functions, HAVING operators and join types.")]
    public Task<string> ListOptions(CancellationToken cancel = default) =>
        Read("listed the options", s => new JsonObject
        {
            ["engine"] = Db.DisplayName(s.Engine),
            ["derivations"] = new JsonArray(Dialect.Derivations(s.Engine).Select(d => (JsonNode)(DerivedFieldViewModel.ParameterRequired.Contains(d) ? d + " (parameter)" : d)).ToArray()),
            ["filter_operators"] = new JsonArray(FilterConditionViewModel.Operators.Select(o => (JsonNode)o).ToArray()),
            ["casts"] = new JsonArray(Casts.Select(o => (JsonNode)o).ToArray()),
            ["case_operators"] = new JsonArray(CaseOperators.Select(o => (JsonNode)o).ToArray()),
            ["aggregates"] = new JsonArray(AggregateViewModel.Functions.Select(o => (JsonNode)o).ToArray()),
            ["having_operators"] = new JsonArray(HavingViewModel.Operators.Select(o => (JsonNode)o).ToArray()),
            ["joins"] = new JsonArray(JoinTypes.Select(o => (JsonNode)o).ToArray()),
        }, cancel);

    // ───────────────────────────── building ─────────────────────────────

    [McpServerTool(Name = "build_query")]
    [Description("Builds a whole query in one call, replacing what's on the diagram (one undo step): a base table and its columns, lookups (joins), filters, derived fields, CASE expressions, grouping with counts or sums, sorting, a row limit and DISTINCT. The quickest way to answer a question; the other tools change one part afterwards. Give the parts directly (table, columns, filters…) or all in query. Fails if there's a query on the diagram already, unless replace is true.")]
    public Task<string> BuildQuery(
        [Description("The base table everything starts from: \"patients\".")] string? table = null,
        [Description("Its columns to show. Default: all of them (none when grouping).")] List<string>? columns = null,
        [Description("Column headings: {\"last_name\": \"Last Name\"}. Optional.")] Dictionary<string, string>? aliases = null,
        [Description("Joins to other tables, in order: the first is LOOKUP_1. Optional.")] List<LookupSpec>? lookups = null,
        [Description("WHERE conditions. Optional.")] List<ConditionSpec>? filters = null,
        [Description("AND (the default) or OR, between the filters.")] string? combiner = null,
        [Description("Derived fields. Optional.")] List<DerivedSpec>? derived = null,
        [Description("CASE expressions. Optional.")] List<CaseSpec>? case_when = null,
        [Description("Grouping, with counts or sums per group. Optional.")] GroupSpec? group_by = null,
        [Description("The order of the rows. Optional.")] List<SortSpec>? sort = null,
        [Description("Only the first N rows. Optional.")] int? limit = null,
        [Description("Leave out duplicate rows. Default false.")] bool? distinct = null,
        [Description("The whole query as one object (table, columns, lookups, filters…), instead of the parts above.")] QuerySpec? query = null,
        [Description("Replace the query on the diagram. Default false.")] bool replace = false,
        CancellationToken cancel = default)
    {
        query ??= new QuerySpec
        {
            Table = table ?? throw new McpException("build_query needs table: the base table the query starts from (list_tables shows them)."),
            Columns = columns, Aliases = aliases, Lookups = lookups, Filters = filters, Combiner = combiner, Derived = derived,
            CaseWhen = case_when, GroupBy = group_by, Sort = sort, Limit = limit, Distinct = distinct,
        };
        return Build(query, replace, cancel);
    }

    private Task<string> Build(QuerySpec query, bool replace, CancellationToken cancel) =>
        Edit("built a query", [query.Table, .. (query.Lookups ?? []).Select(l => l.Table)], (s, doc, catalog) =>
        {
            if (doc.Entities.Count > 0 && !replace)
                throw new InvalidOperationException("There's a query on the diagram already: give replace true to build over it (undo brings it back), or change parts of it with the other tools.");
            if (string.IsNullOrWhiteSpace(query.Table)) throw new ArgumentException("build_query needs table: the base table the query starts from (list_tables shows them).");
            QueryEdits.Clear(doc);
            var grouping = query.GroupBy is { } g && (g.Fields.Count > 0 || g.Aggregates.Count > 0);
            var table = MakeTable(catalog, query.Table, query.Columns ?? (grouping ? [] : null), query.Aliases, allowEmpty: grouping);
            QueryEdits.AddTable(doc, table);
            if (grouping && table.SelectedColumns.Count == 0) table.SelectedColumns.Add(catalog.Columns(table.TableName)[0].Name);
            foreach (var l in query.Lookups ?? []) QueryEdits.AddLookup(doc, MakeLookup(doc, catalog, l));
            if (query.Filters is { Count: > 0 }) QueryEdits.AddFilter(doc, MakeFilter(doc, catalog, query.Filters, query.Combiner));
            if (query.Derived is { Count: > 0 }) QueryEdits.SetSingle(doc, "Derived", MakeDerived(doc, catalog, query.Derived));
            if (query.CaseWhen is { Count: > 0 }) QueryEdits.SetSingle(doc, "CaseWhen", MakeCase(doc, catalog, query.CaseWhen));
            if (grouping) QueryEdits.SetSingle(doc, "GroupBy", MakeGroup(doc, catalog, query.GroupBy!));
            if (query.Sort is { Count: > 0 }) QueryEdits.SetSingle(doc, "Sorting", MakeSort(doc, catalog, query.Sort));
            if (query.Limit is > 0 and var n) QueryEdits.SetSingle(doc, "Limiter", new LimiterResult { TopCount = n });
            else if (query.Limit is < 0) throw new ArgumentException("limit is a number of rows, 1 or more.");
            if (query.Distinct == true) QueryEdits.SetSingle(doc, "Distinct", new DistinctResult());
            return new JsonObject { ["entities"] = new JsonArray(doc.Entities.Select(e => (JsonNode)$"{e.Id}: {e.Label}").ToArray()) };
        }, cancel);

    [McpServerTool(Name = "add_table")]
    [Description("Adds a table. The first is the base table every lookup, filter and sort hangs off (as Add Table Source does); another is listed beside it in FROM, without a join (use add_lookup to join tables).")]
    public Task<string> AddTable(
        [Description("The table or view: \"patients\" or \"dbo.Patients\".")] string table,
        [Description("Its columns to show. Default: all of them.")] List<string>? columns = null,
        [Description("Column headings: {\"last_name\": \"Last Name\"}. Optional.")] Dictionary<string, string>? aliases = null,
        CancellationToken cancel = default) =>
        Edit("added a table", [table], (s, doc, catalog) =>
        {
            var t = MakeTable(catalog, table, columns, aliases);
            var e = QueryEdits.AddTable(doc, t);
            return new JsonObject { ["id"] = e.Id, ["table"] = t.TableName, ["columns"] = t.SelectedColumns.Count, ["base"] = ReferenceEquals(e, QueryEdits.BaseTable(doc)) };
        }, cancel);

    [McpServerTool(Name = "set_columns")]
    [Description("Changes which of a table's columns the query shows, and their headings.")]
    public Task<string> SetColumns(
        [Description("The columns to show, in order.")] List<string> columns,
        [Description("Column headings: {\"dob\": \"Date of Birth\"}. Optional.")] Dictionary<string, string>? aliases = null,
        [Description("The table's id (get_query). Default: the base table.")] string? id = null,
        CancellationToken cancel = default) =>
        Edit("changed the columns", [], (s, doc, catalog) =>
        {
            var e = id is null ? QueryEdits.BaseTable(doc) ?? throw new InvalidOperationException("There's no table yet: add_table first.") : Entity(doc, id, "Table");
            var t = MakeTable(catalog, e.TableSource!.TableName, columns, aliases);
            t.IsView = e.TableSource.IsView;
            QueryEdits.Replace(doc, e, t);
            return new JsonObject { ["id"] = e.Id, ["columns"] = new JsonArray(t.SelectedColumns.Select(c => (JsonNode)c).ToArray()) };
        }, cancel);

    [McpServerTool(Name = "add_lookup")]
    [Description("Joins another table to the base table (Add Connected Source): the base table's from column(s) match the lookup table's on column, and the lookup's columns come back as LOOKUP_n.column (n counts the lookups). LEFT keeps every base row; INNER only those with a match. describe_table suggests lookups from the foreign keys.")]
    public Task<string> AddLookup(
        [Description("The table to look up in: \"providers\".")] string table,
        [Description("Its column the base table's match: \"provider_id\".")] string on,
        [Description("The base table's column(s) that point at it: [\"provider_id\"].")] List<string> from,
        [Description("Its columns to return: [\"name\"].")] List<string> columns,
        [Description("Headings for the returned columns: {\"name\": \"Provider\"}. Optional.")] Dictionary<string, string>? aliases = null,
        [Description("LEFT (the default), INNER, RIGHT or FULL.")] string? join = null,
        CancellationToken cancel = default) =>
        Edit("added a lookup", [table], (s, doc, catalog) =>
        {
            var l = MakeLookup(doc, catalog, new LookupSpec { Table = table, On = on, From = from, Columns = columns, Aliases = aliases, Join = join });
            var e = QueryEdits.AddLookup(doc, l);
            return new JsonObject { ["id"] = e.Id, ["alias"] = $"LOOKUP_{l.OrdinalValue}", ["fields"] = new JsonArray(l.ReturnFields.Select(f => (JsonNode)$"LOOKUP_{l.OrdinalValue}.{f}").ToArray()) };
        }, cancel);

    [McpServerTool(Name = "set_lookup")]
    [Description("Changes a lookup: its join type, returned columns, headings or the columns it joins on. What isn't given stays as it is.")]
    public Task<string> SetLookup(
        [Description(IdHelp)] string id,
        [Description("Its column the base table's match. Optional.")] string? on = null,
        [Description("The base table's column(s) that point at it. Optional.")] List<string>? from = null,
        [Description("Its columns to return. Optional.")] List<string>? columns = null,
        [Description("Headings for the returned columns. Optional.")] Dictionary<string, string>? aliases = null,
        [Description("LEFT, INNER, RIGHT or FULL. Optional.")] string? join = null,
        CancellationToken cancel = default) =>
        Edit("changed a lookup", [], (s, doc, catalog) =>
        {
            var e = Entity(doc, id, "Lookup");
            var was = e.ConnectedSource!;
            var l = MakeLookup(doc, catalog, new LookupSpec
            {
                Table = was.LookupTableName, On = on ?? was.JoinFieldInLookup, From = from ?? was.JoinFieldsFromSource,
                Columns = columns ?? was.ReturnFields, Aliases = aliases ?? (columns is null ? was.ColumnAliases : null), Join = join ?? was.JoinType,
            });
            QueryEdits.Replace(doc, e, l);
            return new JsonObject { ["id"] = e.Id, ["alias"] = $"LOOKUP_{l.OrdinalValue}" };
        }, cancel);

    [McpServerTool(Name = "add_filter")]
    [Description("Adds a WHERE filter: its conditions joined by AND (or OR). Each filter is bracketed and ANDed with the query's other filters, so (a OR b) AND c is two filters. Values are quoted for you.")]
    public Task<string> AddFilter(
        [Description("The conditions.")] List<ConditionSpec> conditions,
        [Description("AND (the default) or OR, between this filter's conditions.")] string? combiner = null,
        CancellationToken cancel = default) =>
        Edit("added a filter", [], (s, doc, catalog) =>
        {
            var e = QueryEdits.AddFilter(doc, MakeFilter(doc, catalog, conditions, combiner));
            return new JsonObject { ["id"] = e.Id, ["label"] = e.Label };
        }, cancel);

    [McpServerTool(Name = "set_filter")]
    [Description("Replaces a filter's conditions (and combiner).")]
    public Task<string> SetFilter(
        [Description(IdHelp)] string id,
        [Description("The conditions.")] List<ConditionSpec> conditions,
        [Description("AND or OR. Default: as it was.")] string? combiner = null,
        CancellationToken cancel = default) =>
        Edit("changed a filter", [], (s, doc, catalog) =>
        {
            var e = Entity(doc, id, "Filter");
            QueryEdits.Replace(doc, e, MakeFilter(doc, catalog, conditions, combiner ?? e.Filter!.Combiner));
            return new JsonObject { ["id"] = e.Id, ["label"] = e.Label };
        }, cancel);

    [McpServerTool(Name = "set_derived")]
    [Description("Sets the query's derived fields (all of them: the ones there are replaced), each a function of a field with a heading: the year of a date, a name in capitals, years between two dates. An empty list removes them.")]
    public Task<string> SetDerived(
        [Description("The derived fields.")] List<DerivedSpec> fields,
        CancellationToken cancel = default) =>
        Edit("set the derived fields", [], (s, doc, catalog) =>
        {
            var e = QueryEdits.SetSingle(doc, "Derived", fields.Count == 0 ? null : MakeDerived(doc, catalog, fields));
            return new JsonObject { ["id"] = e?.Id, ["removed"] = e is null ? true : null };
        }, cancel);

    [McpServerTool(Name = "set_case_when")]
    [Description("Sets the query's CASE expressions (all of them), each turning a field's values into labels (\"A\" → \"Active\"). An empty list removes them.")]
    public Task<string> SetCaseWhen(
        [Description("The CASE expressions.")] List<CaseSpec> expressions,
        CancellationToken cancel = default) =>
        Edit("set the CASE expressions", [], (s, doc, catalog) =>
        {
            var e = QueryEdits.SetSingle(doc, "CaseWhen", expressions.Count == 0 ? null : MakeCase(doc, catalog, expressions));
            return new JsonObject { ["id"] = e?.Id, ["removed"] = e is null ? true : null };
        }, cancel);

    [McpServerTool(Name = "set_group_by")]
    [Description("Groups the rows (GROUP BY) and shows aggregates per group: counts, sums, averages, the earliest or latest. The query then shows the group fields and aggregates only. HAVING keeps the groups whose aggregates pass. Empty fields and aggregates remove the grouping.")]
    public Task<string> SetGroupBy(
        [Description("The fields to group by (fields, or derived fields' and CASE expressions' aliases).")] List<string> fields,
        [Description("The aggregates: [{\"function\": \"COUNT(*)\", \"alias\": \"Visits\"}].")] List<AggregateSpec> aggregates,
        [Description("Conditions on the aggregates. Optional.")] List<HavingSpec>? having = null,
        [Description("AND (the default) or OR between the having conditions.")] string? having_combiner = null,
        CancellationToken cancel = default) =>
        Edit("set the grouping", [], (s, doc, catalog) =>
        {
            var g = fields.Count == 0 && aggregates.Count == 0 ? null
                : MakeGroup(doc, catalog, new GroupSpec { Fields = fields, Aggregates = aggregates, Having = having, HavingCombiner = having_combiner });
            var e = QueryEdits.SetSingle(doc, "GroupBy", g);
            return new JsonObject { ["id"] = e?.Id, ["removed"] = e is null ? true : null };
        }, cancel);

    [McpServerTool(Name = "set_sort")]
    [Description("Sets the order of the rows (ORDER BY), first field first. An empty list removes it.")]
    public Task<string> SetSort(
        [Description("The sort fields: [{\"field\": \"last_name\"}, {\"field\": \"Visits\", \"direction\": \"DESC\"}].")] List<SortSpec> fields,
        CancellationToken cancel = default) =>
        Edit("set the sort order", [], (s, doc, catalog) =>
        {
            var e = QueryEdits.SetSingle(doc, "Sorting", fields.Count == 0 ? null : MakeSort(doc, catalog, fields));
            return new JsonObject { ["id"] = e?.Id, ["removed"] = e is null ? true : null };
        }, cancel);

    [McpServerTool(Name = "set_limit")]
    [Description("Returns only the first N rows (TOP N on SQL Server, LIMIT N elsewhere); 0 removes the limit.")]
    public Task<string> SetLimit(
        [Description("The number of rows; 0 for no limit.")] int rows,
        CancellationToken cancel = default) =>
        Edit("set the row limit", [], (s, doc, catalog) =>
        {
            if (rows < 0) throw new ArgumentException("rows is 0 (no limit) or more.");
            var e = QueryEdits.SetSingle(doc, "Limiter", rows == 0 ? null : new LimiterResult { TopCount = rows });
            return new JsonObject { ["id"] = e?.Id, ["removed"] = e is null ? true : null };
        }, cancel);

    [McpServerTool(Name = "set_distinct")]
    [Description("Turns SELECT DISTINCT on (duplicate rows left out) or off.")]
    public Task<string> SetDistinct(
        [Description("true for DISTINCT, false to remove it.")] bool on,
        CancellationToken cancel = default) =>
        Edit(on ? "turned DISTINCT on" : "turned DISTINCT off", [], (s, doc, catalog) =>
        {
            var e = QueryEdits.SetSingle(doc, "Distinct", on ? new DistinctResult() : null);
            return new JsonObject { ["id"] = e?.Id, ["distinct"] = on };
        }, cancel);

    [McpServerTool(Name = "remove", Destructive = true)]
    [Description("Removes an entity from the diagram. Removing the base table removes everything (the rest hangs off it): in the open window the person is asked first.")]
    public async Task<string> Remove(
        McpServer server,
        [Description(IdHelp)] string id,
        CancellationToken cancel = default)
    {
        var (isBase, count) = await Run("read the query", s =>
        {
            var doc = s.Document;
            var e = Entity(doc, id, null);
            return (ReferenceEquals(e, QueryEdits.BaseTable(doc)), doc.Entities.Count);
        }, cancel).ConfigureAwait(false);
        if (isBase && count > 1 && host.Live)
            await Confirm(server, $"Remove the base table and with it the whole query ({count} entities)? Go ahead?", cancel).ConfigureAwait(false);
        return await Edit("removed an entity", [], (s, doc, catalog) =>
        {
            var e = Entity(doc, id, null);
            var label = e.Label;
            var all = ReferenceEquals(e, QueryEdits.BaseTable(doc));
            QueryEdits.Delete(doc, e);
            return new JsonObject { ["removed"] = label, ["cleared"] = all ? true : null, ["entities"] = doc.Entities.Count };
        }, cancel, needsDatabase: false).ConfigureAwait(false);
    }

    // ───────────────────────────── checking and running ─────────────────────────────

    [McpServerTool(Name = "check_query", ReadOnly = true, Idempotent = true)]
    [Description("Asks the database whether the query's SQL is valid, without reading its rows: ok and the columns it would return, or the database's error. Run it after building.")]
    public async Task<string> CheckQuery(CancellationToken cancel = default)
    {
        var (engine, cs, sql) = await Connected(cancel).ConfigureAwait(false);
        try
        {
            var rows = await Task.Run(() => Db.RunReadOnly(engine, cs, sql, 0, 0), cancel).ConfigureAwait(false);
            return new JsonObject { ["ok"] = true, ["columns"] = new JsonArray(rows.Columns.Select(c => (JsonNode)c).ToArray()), ["sql"] = sql }.ToJsonString(Json.Options);
        }
        catch (System.Data.Common.DbException e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message, ["sql"] = sql }.ToJsonString(Json.Options);
        }
    }

    [McpServerTool(Name = "run_query", ReadOnly = true)]
    [Description("Runs the query, read-only (inside a transaction that is rolled back), and shows the rows to the person in the Results Grid. You get how many rows there are and the columns; the rows themselves only if the person lets AI agents see query results (AI menu), up to max_rows.")]
    public async Task<string> RunQuery(
        [Description("How many rows to give you, if you may see them: 1 to 200. Default 20.")] int max_rows = 20,
        CancellationToken cancel = default)
    {
        if (max_rows is < 0 or > MaxRowsToAgent) throw new McpException($"max_rows is from 0 to {MaxRowsToAgent}.");
        var (engine, cs, sql) = await Connected(cancel).ConfigureAwait(false);
        var see = await Run("read the settings", s => s.AgentsSeeRows, cancel).ConfigureAwait(false);
        QueryRows rows;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            rows = await Task.Run(() => Db.RunReadOnly(engine, cs, sql, host.Live ? GridRows : max_rows), cancel).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException e)
        {
            throw new McpException($"The database rejected the query: {e.Message}\nSQL:\n{sql}");
        }
        await Run("ran the query", s =>
        {
            s.ShowRows(rows, sql);
            return true;
        }, cancel).ConfigureAwait(false);
        var answer = new JsonObject
        {
            ["rows"] = rows.Count,
            ["more_than"] = rows.More ? true : null,
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2),
            ["columns"] = new JsonArray(rows.Columns.Select(c => (JsonNode)c).ToArray()),
        };
        if (see && max_rows > 0)
            answer["data"] = new JsonArray(rows.Rows.Take(max_rows).Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)v).ToArray())).ToArray());
        else
            answer["note"] = see ? null : "The rows are shown to the person, not to you: they haven't let AI agents see query results (AI › Let AI Agents See Query Results). Check the query by its row count and columns, and ask the person what they see.";
        return answer.ToJsonString(Json.Options);
    }

    /// <summary>How many rows the window's grid gets from an agent's run.</summary>
    private const int GridRows = 10_000;

    [McpServerTool(Name = "export_results")]
    [Description("Runs the query (read-only) and writes every row to a file in the working folder: .csv, or .xlsx for Excel. Won't write over a file it didn't write unless overwrite is true. The file is the person's: if they haven't let AI agents see results, read_file won't open it for you.")]
    public async Task<string> ExportResults(
        [Description("The file, relative to the working folder: \"exports/active-patients.xlsx\".")] string path,
        [Description("Write over an existing file. Default false.")] bool overwrite = false,
        CancellationToken cancel = default)
    {
        var full = _files.Resolve(path, "An export", ".csv", ".xlsx");
        _files.CheckWritable(full, overwrite);
        var (engine, cs, sql) = await Connected(cancel).ConfigureAwait(false);
        var see = await Run("read the settings", s => s.AgentsSeeRows, cancel).ConfigureAwait(false);
        var rows = await Task.Run(() => Db.RunReadOnly(engine, cs, sql, int.MaxValue, 1_000_000), cancel).ConfigureAwait(false);
        await Task.Run(() => Export.Write(full, rows), cancel).ConfigureAwait(false);
        _files.Wrote(full);
        if (!see) lock (_hiddenExports) _hiddenExports.Add(full);
        return new JsonObject { ["exported"] = Relative(full), ["rows"] = rows.Rows.Count, ["more_than"] = rows.More ? true : null }.ToJsonString(Json.Options);
    }

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true)]
    [Description("A picture of the query's diagram as the window draws it (the base table, its lookups and the other entities joined to it), with the SQL beside it in text. Look at it to check the query is what was asked for.")]
    public async Task<CallToolResult> RenderView(
        [Description("Scale, from 0.3 to 2. Default 1.")] double scale = 1,
        CancellationToken cancel = default)
    {
        if (scale is < 0.3 or > 2) throw new McpException("scale is from 0.3 to 2.");
        var (png, sql) = await Run("looked at the query", s => (host.Library.Picture(s.Document, scale), QueryEdits.Sql(s.Document, s.Engine)), cancel).ConfigureAwait(false);
        return new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = sql.Length == 0 ? "(no SQL yet: add a table)" : sql }],
        };
    }

    // ───────────────────────────── files ─────────────────────────────

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true)]
    [Description("Lists files in the working folder (or a folder inside it): saved queries (.qry), SQLite databases, SQL scripts, specs. Optionally only those matching a pattern such as \"*.qry\", and through sub-folders.")]
    public Task<string> ListFiles(
        [Description("A folder inside the working folder. Default: the working folder.")] string folder = ".",
        [Description("A file name pattern, such as \"*.qry\" or \"*.db\". Default: every file.")] string pattern = "*",
        [Description("Look through sub-folders too. Default false.")] bool recursive = false,
        CancellationToken cancel = default) =>
        Read("listed files", _ =>
        {
            var full = folder is "." or "" ? _files.Root : _files.Resolve(folder);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"There's no folder {folder} in the working folder.");
            var skip = new[] { "bin", "obj", "node_modules", ".git", ".vs", ".idea" };
            var files = Directory.EnumerateFiles(full, pattern, new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true })
                .Where(f => !Path.GetRelativePath(full, f).Split(Path.DirectorySeparatorChar).Any(part => skip.Contains(part, StringComparer.OrdinalIgnoreCase)))
                .Take(501).ToList();
            var folders = recursive ? [] : Directory.EnumerateDirectories(full).Select(Path.GetFileName).Where(n => n is not null && !skip.Contains(n, StringComparer.OrdinalIgnoreCase)).Take(200).ToList();
            return new JsonObject
            {
                ["folder"] = Path.GetRelativePath(_files.Root, full),
                ["folders"] = new JsonArray(folders.Select(f => (JsonNode)f!).ToArray()),
                ["files"] = new JsonArray(files.Take(500).Select(f => (JsonNode)new JsonObject { ["path"] = Path.GetRelativePath(_files.Root, f), ["bytes"] = new FileInfo(f).Length }).ToArray()),
                ["more"] = files.Count > 500 ? "More than 500: narrow it with a pattern or a folder." : null,
            };
        }, cancel);

    /// <summary>How many characters read_file returns at most in one call.</summary>
    internal const int ReadLimit = 60_000;

    [McpServerTool(Name = "read_file", ReadOnly = true, Idempotent = true)]
    [Description("Reads a text file in the working folder (a SQL script, a report spec, notes on what a query should do), with its lines numbered. A long file comes in pieces: give start_line to read on from where the last piece ended.")]
    public Task<string> ReadFile(
        [Description("The file, relative to the working folder.")] string path,
        [Description("The first line to read, from 1. Default 1.")] int start_line = 1,
        [Description("At most this many lines. Default 600.")] int max_lines = 600,
        CancellationToken cancel = default) =>
        Read("read a file", _ =>
        {
            var full = _files.Resolve(path);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no {path} in the working folder. list_files shows what is there.");
            lock (_hiddenExports)
                if (_hiddenExports.Contains(full))
                    throw new UnauthorizedAccessException($"{path} holds a query's results, which the person hasn't let AI agents see.");
            var info = new FileInfo(full);
            if (info.Length > 20_000_000) throw new IOException($"{path} is {info.Length / 1_000_000} MB: too big to read here.");
            var bytes = File.ReadAllBytes(full);
            if (bytes.Take(8000).Any(b => b == 0)) throw new NotSupportedException($"{path} isn't text (it has binary data in it).");
            var all = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
            if (start_line < 1 || start_line > Math.Max(1, all.Length)) throw new ArgumentException($"start_line is from 1 to {all.Length}.");
            var take = Math.Clamp(max_lines, 1, 5000);
            var text = new StringBuilder();
            var last = start_line - 1;
            for (var i = start_line - 1; i < all.Length && i < start_line - 1 + take; i++)
            {
                var line = $"{i + 1,5}  {all[i]}\n";
                if (text.Length + line.Length > ReadLimit && text.Length > 0) break;
                text.Append(line);
                last = i + 1;
            }
            return new JsonObject
            {
                ["path"] = Path.GetRelativePath(_files.Root, full),
                ["lines"] = all.Length,
                ["from"] = start_line,
                ["to"] = last,
                ["text"] = text.ToString(),
                ["more"] = last < all.Length ? $"Lines {last + 1} to {all.Length} remain: call again with start_line {last + 1}." : null,
            };
        }, cancel);

    // ───────────────────────────── history ─────────────────────────────

    [McpServerTool(Name = "undo")]
    [Description("Takes back the last change to the query (anyone's: the person's or an agent's), as Ctrl+Z does.")]
    public Task<string> Undo(CancellationToken cancel = default) =>
        Read("undid a change", s =>
        {
            if (!s.History.CanUndo) throw new InvalidOperationException("There's nothing to undo.");
            var what = s.History.UndoLabel;
            s.History.Undo();
            s.Refresh();
            return new JsonObject { ["undone"] = what, ["sql"] = QueryEdits.Sql(s.Document, s.Engine) };
        }, cancel);

    [McpServerTool(Name = "redo")]
    [Description("Puts back the change undo last took back.")]
    public Task<string> Redo(CancellationToken cancel = default) =>
        Read("redid a change", s =>
        {
            if (!s.History.CanRedo) throw new InvalidOperationException("There's nothing to redo.");
            var what = s.History.RedoLabel;
            s.History.Redo();
            s.Refresh();
            return new JsonObject { ["redone"] = what, ["sql"] = QueryEdits.Sql(s.Document, s.Engine) };
        }, cancel);

    // ───────────────────────────── making entities ─────────────────────────────

    private static readonly string[] Casts = ["(none)", "VARCHAR", "INT", "DATE", "DATETIME", "DECIMAL"];
    private static readonly string[] CaseOperators = ["=", "!=", "<", ">", "<=", ">=", "LIKE", "IS NULL", "IS NOT NULL"];
    private static readonly string[] JoinTypes = ["LEFT JOIN", "INNER JOIN", "RIGHT JOIN", "FULL OUTER JOIN"];

    private static TableSourceResult MakeTable(Catalog catalog, string table, List<string>? columns, Dictionary<string, string>? aliases, bool allowEmpty = false)
    {
        Guard.Identifier(table, "A table");
        var t = catalog.Table(table);
        var all = catalog.Columns(t.FullName);
        var chosen = columns is null ? all.Select(c => c.Name).ToList() : columns.Select(c => catalog.Column(t.FullName, Bare(c, t))).Distinct().ToList();
        if (chosen.Count == 0 && !allowEmpty) throw new ArgumentException($"Choose at least one of {t.FullName}'s columns.");
        return new TableSourceResult
        {
            TableName = t.FullName,
            IsView = t.IsView,
            SelectedColumns = chosen,
            ColumnAliases = Aliases(aliases, c => catalog.Column(t.FullName, Bare(c, t)), chosen),
        };
    }

    /// <summary>A column named with its table ("patients.last_name") as its bare name, when the table is this one.</summary>
    private static string Bare(string column, TableInfo t)
    {
        var parts = Catalog.Unquote(column).Split('.');
        if (parts.Length == 1) return parts[0];
        var qualifier = string.Join('.', parts[..^1]);
        if (qualifier.Equals(t.FullName, StringComparison.OrdinalIgnoreCase) || qualifier.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) return parts[^1];
        throw new ArgumentException($"\"{column}\" isn't a column of {t.FullName}.");
    }

    private static Dictionary<string, string> Aliases(Dictionary<string, string>? given, Func<string, string> column, List<string> chosen)
    {
        var map = new Dictionary<string, string>();
        foreach (var (k, v) in given ?? [])
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var c = column(k);
            if (!chosen.Contains(c)) throw new ArgumentException($"The heading for {k} is for a column that isn't shown: add it to the columns.");
            map[c] = v.Trim();
        }
        return map;
    }

    private static ConnectedSourceResult MakeLookup(QueryFile doc, Catalog catalog, LookupSpec spec)
    {
        var baseTable = QueryEdits.BaseTable(doc)?.TableSource ?? throw new InvalidOperationException("Add a base table first: a lookup joins to it.");
        Guard.Identifier(spec.Table, "A lookup's table");
        var t = catalog.Table(spec.Table);
        var baseInfo = catalog.Table(baseTable.TableName);
        if (spec.From.Count == 0) throw new ArgumentException($"Say which of {baseTable.TableName}'s columns point at {t.FullName} (from).");
        if (spec.Columns.Count == 0) throw new ArgumentException($"Say which of {t.FullName}'s columns to return (columns).");
        var returned = spec.Columns.Select(c => catalog.Column(t.FullName, Bare(c, t))).Distinct().ToList();
        return new ConnectedSourceResult
        {
            SourceTableName = baseTable.TableName,
            JoinFieldsFromSource = spec.From.Select(f => catalog.Column(baseInfo.FullName, Bare(f, baseInfo))).ToList(),
            LookupTableName = t.FullName,
            JoinFieldInLookup = catalog.Column(t.FullName, Bare(spec.On, t)),
            JoinType = JoinType(spec.Join),
            ReturnFields = returned,
            ColumnAliases = Aliases(spec.Aliases, c => catalog.Column(t.FullName, Bare(c, t)), returned),
        };
    }

    private static string JoinType(string? join)
    {
        var j = (join ?? "LEFT").Trim().ToUpperInvariant().Replace(" JOIN", "").Replace("_", " ");
        return j switch
        {
            "LEFT" or "LEFT OUTER" => "LEFT JOIN",
            "INNER" => "INNER JOIN",
            "RIGHT" or "RIGHT OUTER" => "RIGHT JOIN",
            "FULL" or "FULL OUTER" or "OUTER" => "FULL OUTER JOIN",
            _ => throw new ArgumentException($"\"{join}\" isn't a join: LEFT, INNER, RIGHT or FULL."),
        };
    }

    private static FilterResult MakeFilter(QueryFile doc, Catalog catalog, List<ConditionSpec> conditions, string? combiner)
    {
        if (conditions.Count == 0) throw new ArgumentException("A filter needs at least one condition.");
        var f = new FilterResult { Combiner = Combiner(combiner) };
        foreach (var c in conditions)
        {
            var op = Operator(c.Op ?? "=", FilterConditionViewModel.Operators, "a filter");
            var cond = new FilterCondition { Field = Fields.Resolve(doc, catalog, c.Field), Operator = op, CastAs = Cast(c.Cast) };
            switch (op)
            {
                case "IS NULL" or "IS NOT NULL" or "IS EMPTY" or "IS NOT EMPTY":
                    break;
                case "IN" or "NOT IN":
                    cond.Value = c.Values is { Count: > 0 } list ? string.Join(",", list.Select(v => v.Trim())) : c.Value ?? throw new ArgumentException($"{op} needs values.");
                    break;
                case "BETWEEN":
                    cond.Value = c.Value ?? throw new ArgumentException("BETWEEN needs value and value2.");
                    cond.Value2 = c.Value2 ?? throw new ArgumentException("BETWEEN needs value2, the upper value.");
                    break;
                default:
                    cond.Value = c.Value ?? throw new ArgumentException($"The condition on {c.Field} needs a value.");
                    break;
            }
            f.Conditions.Add(cond);
        }
        return f;
    }

    private static string Combiner(string? combiner) => (combiner ?? "AND").Trim().ToUpperInvariant() switch
    {
        "AND" => "AND",
        "OR" => "OR",
        _ => throw new ArgumentException("The combiner is AND or OR."),
    };

    private static string Operator(string op, IEnumerable<string> allowed, string what)
    {
        var o = op.Trim().ToUpperInvariant() switch
        {
            "<>" or "NOT EQUAL" or "NOT EQUALS" => "!=",
            "==" or "EQUALS" or "IS" => "=",
            "NOT_LIKE" => "NOT LIKE",
            "NOT_IN" => "NOT IN",
            "IS_NULL" or "NULL" => "IS NULL",
            "IS_NOT_NULL" or "NOT NULL" => "IS NOT NULL",
            "EMPTY" => "IS EMPTY",
            "NOT EMPTY" => "IS NOT EMPTY",
            var x => x,
        };
        return allowed.FirstOrDefault(a => a == o) ?? throw new ArgumentException($"\"{op}\" isn't an operator for {what}: {string.Join(", ", allowed)}.");
    }

    private static string Cast(string? cast)
    {
        if (string.IsNullOrWhiteSpace(cast)) return "(none)";
        var c = cast.Trim().ToUpperInvariant() switch { "TEXT" or "STRING" or "NVARCHAR" => "VARCHAR", "INTEGER" => "INT", "TIMESTAMP" => "DATETIME", "NUMERIC" => "DECIMAL", var x => x };
        return Casts.Contains(c) ? c : throw new ArgumentException($"\"{cast}\" isn't a cast: VARCHAR, INT, DATE, DATETIME or DECIMAL.");
    }

    private static DerivedFieldResult MakeDerived(QueryFile doc, Catalog catalog, List<DerivedSpec> fields)
    {
        var result = new DerivedFieldResult();
        var offered = Dialect.Derivations(catalog.Engine);
        foreach (var d in fields)
        {
            var derivation = Derivation(d.Derivation, offered, catalog.Engine);
            var needs = DerivedFieldViewModel.ParameterRequired.Contains(derivation);
            if (needs && string.IsNullOrWhiteSpace(d.Parameter)) throw new ArgumentException($"{derivation} needs a parameter.");
            Guard.Sql(d.Parameter, "A derivation's parameter");
            var parameter = needs ? d.Parameter!.Trim() : "";
            // A parameter naming a field of the query is written as the query names it.
            if (needs && derivation is not ("ISNULL (Default)") && LooksLikeName(parameter))
                try { parameter = Fields.Resolve(doc, catalog, parameter); } catch (ArgumentException) { }
            result.Fields.Add(new DerivedField
            {
                SourceField = Fields.Resolve(doc, catalog, d.Field),
                Derivation = derivation,
                Parameter = parameter,
                Alias = d.Alias?.Trim() ?? "",
            });
        }
        return result;
    }

    private static bool LooksLikeName(string p) => p.Length > 0 && (char.IsLetter(p[0]) || p[0] == '_') && p.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.');

    private static string Derivation(string name, List<string> offered, DbEngine engine)
    {
        static string Key(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var k = Key(name);
        var synonyms = new Dictionary<string, string>
        {
            ["upper"] = "UPPER CASE", ["uppercase"] = "UPPER CASE", ["lower"] = "lower case", ["lowercase"] = "lower case",
            ["year"] = "Year Part", ["month"] = "Month Part", ["day"] = "Day Part", ["hour"] = "Hour Part",
            ["date"] = "Date Only", ["dateonly"] = "Date Only", ["len"] = "Length", ["left"] = "Left N Chars", ["right"] = "Right N Chars",
            ["abs"] = "Absolute Value", ["round"] = "Round to N Decimals", ["ceil"] = "Ceiling", ["isnull"] = "ISNULL (Default)",
            ["coalesce"] = "COALESCE (Fallback)", ["days"] = "DATEDIFF (Days)", ["months"] = "DATEDIFF (Months)", ["years"] = "DATEDIFF (Years)",
            ["age"] = "DATEDIFF (Years)",
        };
        var hit = offered.FirstOrDefault(o => Key(o) == k) ?? (synonyms.TryGetValue(k, out var s) ? s : null);
        if (hit is null || !offered.Contains(hit))
            throw new ArgumentException($"\"{name}\" isn't a derivation {Db.DisplayName(engine)} offers. They are: {string.Join(", ", offered)}.");
        return hit;
    }

    private static CaseWhenResult MakeCase(QueryFile doc, Catalog catalog, List<CaseSpec> expressions)
    {
        var result = new CaseWhenResult();
        foreach (var c in expressions)
        {
            if (c.Cases.Count == 0) throw new ArgumentException($"The CASE on {c.Field} needs at least one WHEN … THEN ….");
            result.Expressions.Add(new CaseExpression
            {
                Field = Fields.Resolve(doc, catalog, c.Field),
                Conditions = c.Cases.Select(w => new WhenThen { Operator = Operator(w.Op ?? "=", CaseOperators, "a CASE"), WhenValue = w.When ?? "", ThenValue = w.Then }).ToList(),
                ElseValue = c.Else ?? "",
                Alias = c.Alias?.Trim() ?? "",
            });
        }
        return result;
    }

    private static GroupByResult MakeGroup(QueryFile doc, Catalog catalog, GroupSpec spec)
    {
        // Aggregates and having refer to the derived fields and CASE expressions already on the diagram.
        var g = new GroupByResult { HavingCombiner = Combiner(spec.HavingCombiner) };
        foreach (var f in spec.Fields) g.GroupFields.Add(Fields.Resolve(doc, catalog, f, expressions: true));
        foreach (var a in spec.Aggregates)
        {
            var fn = AggregateViewModel.Functions.FirstOrDefault(x => x.Equals(a.Function.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? (a.Function.Trim().ToUpperInvariant() switch
                {
                    "COUNT_STAR" or "COUNT ALL" or "ROWS" => "COUNT(*)",
                    "COUNT_DISTINCT" or "DISTINCT COUNT" => "COUNT DISTINCT",
                    "AVERAGE" or "MEAN" => "AVG",
                    "TOTAL" => "SUM",
                    _ => throw new ArgumentException($"\"{a.Function}\" isn't an aggregate: {string.Join(", ", AggregateViewModel.Functions)}."),
                });
            if (fn == "COUNT" && string.IsNullOrWhiteSpace(a.Field)) fn = "COUNT(*)";
            var field = fn == "COUNT(*)" ? "" : Fields.Resolve(doc, catalog, a.Field ?? throw new ArgumentException($"{fn} needs a field."), expressions: true);
            g.Aggregates.Add(new AggregateField { Function = fn, Field = field, Alias = a.Alias?.Trim() ?? "" });
        }
        foreach (var h in spec.Having ?? [])
        {
            var expr = g.Aggregates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Alias) && x.Alias.Equals(h.Aggregate.Trim(), StringComparison.OrdinalIgnoreCase)) is { } named
                ? Fields.Aggregate(named)
                : g.Aggregates.Select(Fields.Aggregate).FirstOrDefault(x => x.Equals(h.Aggregate.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"\"{h.Aggregate}\" isn't one of the grouping's aggregates ({string.Join(", ", g.Aggregates.Select(x => x.Alias.Length > 0 ? x.Alias : Fields.Aggregate(x)))}).");
            var value = h.Value.Trim();
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) value = QueryBuilder.Literal(h.Value);
            g.HavingConditions.Add(new HavingCondition { Expression = expr, Operator = Operator(h.Op ?? ">", HavingViewModel.Operators, "HAVING"), Value = value });
        }
        if (g.GroupFields.Count == 0 && g.Aggregates.Count == 0) throw new ArgumentException("A grouping needs fields to group by, aggregates, or both.");
        return g;
    }

    private static SortingResult MakeSort(QueryFile doc, Catalog catalog, List<SortSpec> fields)
    {
        var s = new SortingResult();
        foreach (var f in fields)
        {
            var dir = (f.Direction ?? "ASC").Trim().ToUpperInvariant() switch
            {
                "ASC" or "ASCENDING" or "UP" => "ASC",
                "DESC" or "DESCENDING" or "DOWN" => "DESC",
                _ => throw new ArgumentException($"\"{f.Direction}\" isn't a direction: ASC or DESC."),
            };
            s.Fields.Add(new SortingField { Field = Fields.Resolve(doc, catalog, f.Field, expressions: true), Direction = dir });
        }
        return s;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private string Relative(string full) => Path.GetRelativePath(host.Root, full).Replace('\\', '/');

    private static IEnumerable<string> Tables(QueryFile doc) =>
        QueryEdits.All<TableSourceResult>(doc).Select(t => t.TableName).Concat(QueryEdits.All<ConnectedSourceResult>(doc).Select(l => l.LookupTableName));

    private JsonObject Summary(IQuerySession s) => new()
    {
        ["file"] = s.FilePath is { } f ? Relative(f) : null,
        ["connected"] = s.ConnectionString.Length == 0 ? null : Connection(s),
        ["changed"] = s.History.IsDirty,
        ["agents_see_rows"] = s.AgentsSeeRows,
    };

    private static JsonObject Connection(IQuerySession s) => new()
    {
        ["engine"] = Db.DisplayName(s.Engine),
        ["name"] = s.ConnectionName,
        ["connection"] = s.Engine == DbEngine.Sqlite ? Path.GetFileName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(s.ConnectionString).DataSource) : ConnectionStore.Redact(s.ConnectionString),
    };

    /// <summary>An entity as get_query shows it: its id, kind, label and settings.</summary>
    private static JsonObject Entity(EntityState e) => new()
    {
        ["id"] = e.Id,
        ["kind"] = e.EntityType,
        ["label"] = e.Label,
        ["settings"] = QueryEdits.Metadata(e) is { } m ? JsonSerializer.SerializeToNode(m, m.GetType()) : null,
    };

    private static JsonArray Expressions(QueryFile doc, DbEngine engine)
    {
        var list = new JsonArray();
        foreach (var d in QueryEdits.All<DerivedFieldResult>(doc).SelectMany(d => d.Fields))
            list.Add(new JsonObject { ["alias"] = d.Alias, ["sql"] = Dialect.Derive(engine, d.SourceField, d.Derivation, d.Parameter) });
        foreach (var c in QueryEdits.All<CaseWhenResult>(doc).SelectMany(c => c.Expressions))
            list.Add(new JsonObject { ["alias"] = c.Alias, ["sql"] = QueryEdits.CaseSql(c) });
        foreach (var a in QueryEdits.All<GroupByResult>(doc).SelectMany(g => g.Aggregates))
            list.Add(new JsonObject { ["alias"] = a.Alias, ["sql"] = Fields.Aggregate(a) });
        return list;
    }

    private static EntityState Entity(QueryFile doc, string id, string? kind)
    {
        var e = QueryEdits.Find(doc, id) ?? throw new ArgumentException($"There's no entity \"{id}\". The entities: {(doc.Entities.Count == 0 ? "none" : string.Join(", ", doc.Entities.Select(x => $"{x.Id} ({x.Label})")))}.");
        if (kind != null && e.EntityType != kind) throw new ArgumentException($"{id} is a {e.EntityType} ({e.Label}), not a {kind}.");
        return e;
    }

    private static void Discardable(IQuerySession s, bool discard)
    {
        if (s.History.IsDirty && !discard)
            throw new InvalidOperationException("The open query has changes that haven't been saved. Save it first (save_query), or call again with discard_changes true to throw them away.");
    }

    private async Task<(DbEngine Engine, string Cs, string Sql)> Connected(CancellationToken cancel)
    {
        var (engine, cs, sql) = await Run("read the query", s => (s.Engine, s.ConnectionString, QueryEdits.Sql(s.Document, s.Engine)), cancel).ConfigureAwait(false);
        if (cs.Length == 0) throw new McpException(NotConnected);
        if (sql.Length == 0) throw new McpException("There's no query yet: add a table first (build_query or add_table).");
        return (engine, cs, sql);
    }

    private const string NotConnected = "The query isn't connected to a database. list_connections shows the saved ones; connect to one (or a SQLite file in the working folder).";

    private async Task Test(DbEngine engine, string cs, CancellationToken cancel)
    {
        try
        {
            await Task.Run(() => Db.Test(engine, cs), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or ArgumentException or InvalidOperationException)
        {
            throw new McpException($"Couldn't connect to the {Db.DisplayName(engine)} database: {e.Message}");
        }
    }

    private static T Wrap<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that only reads the catalogue, off the window's thread, and answers in JSON.</summary>
    private async Task<string> ReadDb(string action, Func<DbEngine, string, JsonObject> work, CancellationToken cancel)
    {
        var (engine, cs) = await Run(action, s => (s.Engine, s.ConnectionString), cancel).ConfigureAwait(false);
        if (cs.Length == 0) throw new McpException(NotConnected);
        try
        {
            return (await Task.Run(() => work(engine, cs), cancel).ConfigureAwait(false)).ToJsonString(Json.Options);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that changes nothing (or replaces the query wholesale) and answers in JSON.</summary>
    private Task<string> Read(string action, Func<IQuerySession, JsonObject> work, CancellationToken cancel) =>
        Run(action, s => work(s).ToJsonString(Json.Options), cancel);

    /// <summary>
    /// Runs a change as one undo step. The catalogue (the tables, and the columns of those the query uses and those in
    /// <paramref name="need"/>) is read first, off the window's thread; then the work changes a copy of the query, and
    /// only if it succeeds does the copy become the query. A call that fails leaves the query as it was.
    /// </summary>
    private async Task<string> Edit(string label, IEnumerable<string?> need, Func<IQuerySession, QueryFile, Catalog, JsonObject> work, CancellationToken cancel, bool needsDatabase = true)
    {
        var (engine, cs, tables) = await Run("read the query", s => (s.Engine, s.ConnectionString, Tables(s.Document).ToList()), cancel).ConfigureAwait(false);
        Catalog catalog;
        if (cs.Length == 0)
        {
            if (needsDatabase) throw new McpException(NotConnected);
            catalog = new Catalog(engine, []);
        }
        else
        {
            try
            {
                catalog = await Task.Run(() => Catalog.Load(engine, cs, tables.Concat(need.OfType<string>())), cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new McpException(Explain(e), e);
            }
        }
        return await Run(label, s =>
        {
            if (s.Engine != engine || s.ConnectionString != cs) throw new InvalidOperationException("The database changed while the call was being made. Call again.");
            var before = QueryEdits.ToJson(s.Document);
            var doc = s.Document;
            var result = work(s, doc, catalog);
            QueryEdits.Relabel(doc);
            if (QueryEdits.ToJson(doc) == before)
            {
                result["unchanged"] = true;
                return result.ToJsonString(Json.Options);
            }
            s.Apply(doc, "AI: " + label);
            s.Refresh();
            result["sql"] = QueryEdits.Sql(doc, s.Engine);
            return result.ToJsonString(Json.Options);
        }, cancel).ConfigureAwait(false);
    }

    private async Task<T> Run<T>(string action, Func<IQuerySession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Asks the person (MCP elicitation; the in-app chat shows buttons). Throws if they say no or can't be asked.</summary>
    private static async Task Confirm(McpServer server, string question, CancellationToken cancel)
    {
        ElicitResult answer;
        try
        {
            answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = question,
                RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = { } },
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is McpException or InvalidOperationException or NotSupportedException)
        {
            throw new McpException($"{question} The person couldn't be asked ({e.Message}), so nothing was changed.");
        }
        if (answer.Action != "accept")
            throw new McpException($"The person said no: {question.Replace(" Go ahead?", "", StringComparison.Ordinal)}. Nothing was changed. Ask them what they want.");
    }

    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or QueryBusyException or JsonException or InvalidDataException or KeyNotFoundException => e.Message,
        System.Data.Common.DbException => $"The database said: {e.Message}",
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}

/// <summary>Writing a query's rows to a file: CSV, or an Excel workbook (ClosedXML).</summary>
internal static class Export
{
    public static void Write(string path, QueryRows rows)
    {
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var book = new ClosedXML.Excel.XLWorkbook();
            var sheet = book.Worksheets.Add("Results");
            for (var c = 0; c < rows.Columns.Count; c++)
            {
                sheet.Cell(1, c + 1).Value = rows.Columns[c];
                sheet.Cell(1, c + 1).Style.Font.Bold = true;
            }
            for (var r = 0; r < rows.Rows.Count; r++)
                for (var c = 0; c < rows.Columns.Count; c++)
                    sheet.Cell(r + 2, c + 1).Value = rows.Rows[r][c];
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(1, Math.Min(rows.Rows.Count + 1, 200));
            book.SaveAs(path);
            return;
        }
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(",", rows.Columns.Select(Csv)));
        foreach (var row in rows.Rows) w.WriteLine(string.Join(",", row.Select(Csv)));
    }

    private static string Csv(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
