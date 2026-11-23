using System;
using System.Collections.Generic;
using System.Linq;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>
/// The database's tables and the columns of the ones a call needs, read from the catalogue (never rows) before the
/// call touches the query, so the reading happens off the window's thread.
/// </summary>
public sealed class Catalog
{
    private readonly Dictionary<string, List<ColumnDetail>> _columns = new(StringComparer.OrdinalIgnoreCase);

    public Catalog(DbEngine engine, IReadOnlyList<TableInfo> tables)
    {
        Engine = engine;
        Tables = tables;
    }

    public DbEngine Engine { get; }

    public IReadOnlyList<TableInfo> Tables { get; }

    /// <summary>Reads the tables, and the columns of <paramref name="need"/> (names as given: they are resolved first).</summary>
    public static Catalog Load(DbEngine engine, string connectionString, IEnumerable<string> need)
    {
        var catalog = new Catalog(engine, Db.ListTables(engine, connectionString));
        foreach (var name in need.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.TryTable(name) is not { } table) continue;
            if (!catalog._columns.ContainsKey(table.FullName))
                catalog._columns[table.FullName] = Db.DescribeColumns(engine, connectionString, table.FullName);
        }
        return catalog;
    }

    /// <summary>A table by its name as written ("dbo.Patients", "Patients", "patients"), or null.</summary>
    public TableInfo? TryTable(string name)
    {
        var n = Unquote(name);
        var exact = Tables.FirstOrDefault(t => t.FullName.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var bare = Tables.Where(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
        return bare.Count == 1 ? bare[0] : null;
    }

    /// <summary>A table by its name as written, or an error that says which tables there are.</summary>
    public TableInfo Table(string name)
    {
        if (TryTable(name) is { } t) return t;
        var n = Unquote(name);
        var same = Tables.Where(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count > 1)
            throw new ArgumentException($"\"{name}\" is in more than one schema: {string.Join(", ", same.Select(x => x.FullName))}. Give it as schema.table.");
        var near = Tables.Where(t => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(t.Name, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        throw new ArgumentException($"There's no table or view \"{name}\" in the database." +
            (near.Count > 0 ? $" Perhaps: {string.Join(", ", near.Select(x => x.FullName))}." : " list_tables shows them; find_columns searches them."));
    }

    /// <summary>A table's columns (read with the catalogue: name it in the call's needs).</summary>
    public List<ColumnDetail> Columns(string table)
    {
        var t = Table(table);
        return _columns.TryGetValue(t.FullName, out var c) ? c
            : throw new InvalidOperationException($"{t.FullName}'s columns weren't read for this call. Call again.");
    }

    /// <summary>A column of a table by name (any case), as the catalogue spells it, or an error listing the columns.</summary>
    public string Column(string table, string column)
    {
        var cols = Columns(table);
        var c = cols.FirstOrDefault(x => x.Name.Equals(Unquote(column), StringComparison.OrdinalIgnoreCase));
        return c?.Name ?? throw new ArgumentException($"{Table(table).FullName} has no column \"{column}\". Its columns: {string.Join(", ", cols.Select(x => x.Name))}.");
    }

    internal static string Unquote(string name) =>
        string.Join('.', name.Trim().Split('.').Select(p => p.Trim().Trim('[', ']', '"', '`')));
}

/// <summary>
/// Field names as the query writes them, from what an agent writes. A base table's column is "schema.table.column"
/// ("dbo.Patients.LastName"); a lookup's is "LOOKUP_n.column". An agent may write "Patients.LastName",
/// "patients.last_name", "LastName" (when only one table in the query has it) or the lookup's table name in place of
/// LOOKUP_n; derived fields, CASE expressions and aggregates may be named by their alias where an expression is wanted.
/// </summary>
public static class Fields
{
    /// <summary>Every field the query offers: base table columns, then each lookup's.</summary>
    public static List<string> All(QueryFile query, Catalog catalog)
    {
        var list = new List<string>();
        foreach (var t in QueryEdits.All<TableSourceResult>(query))
            if (catalog.TryTable(t.TableName) is { } info)
                list.AddRange(catalog.Columns(info.FullName).Select(c => $"{t.TableName}.{c.Name}"));
        foreach (var l in QueryEdits.All<ConnectedSourceResult>(query).OrderBy(l => l.OrdinalValue))
            if (catalog.TryTable(l.LookupTableName) is { } info)
                list.AddRange(catalog.Columns(info.FullName).Select(c => $"LOOKUP_{l.OrdinalValue}.{c.Name}"));
        return list;
    }

    /// <summary>
    /// A field as the query writes it. With <paramref name="expressions"/>, a derived field's, CASE expression's or
    /// aggregate's alias gives its SQL (for group by and sorting).
    /// </summary>
    public static string Resolve(QueryFile query, Catalog catalog, string text, bool expressions = false)
    {
        Guard.Identifier(text, "A field");
        var t = text.Trim();
        if (expressions && Expression(query, catalog.Engine, t) is { } expr) return expr;
        var parts = Catalog.Unquote(t).Split('.');
        var column = parts[^1];
        var qualifier = parts.Length > 1 ? string.Join('.', parts[..^1]) : null;
        var hits = new List<string>();
        foreach (var table in QueryEdits.All<TableSourceResult>(query))
        {
            if (catalog.TryTable(table.TableName) is not { } info) continue;
            if (qualifier != null && !Names(info).Contains(qualifier, StringComparer.OrdinalIgnoreCase)) continue;
            if (catalog.Columns(info.FullName).FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) is { } c)
                hits.Add($"{table.TableName}.{c.Name}");
        }
        foreach (var lookup in QueryEdits.All<ConnectedSourceResult>(query).OrderBy(l => l.OrdinalValue))
        {
            if (catalog.TryTable(lookup.LookupTableName) is not { } info) continue;
            var alias = $"LOOKUP_{lookup.OrdinalValue}";
            if (qualifier != null && !qualifier.Equals(alias, StringComparison.OrdinalIgnoreCase) && !Names(info).Contains(qualifier, StringComparer.OrdinalIgnoreCase)) continue;
            // A table that is both the base and a lookup (a self-join) answers to its own name as the base table.
            if (qualifier != null && !qualifier.Equals(alias, StringComparison.OrdinalIgnoreCase) && hits.Count > 0) continue;
            if (catalog.Columns(info.FullName).FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) is { } c)
                hits.Add($"{alias}.{c.Name}");
        }
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1)
            throw new ArgumentException($"\"{text}\" could be {string.Join(" or ", hits)}: give the one you mean.");
        var all = All(query, catalog);
        throw new ArgumentException($"\"{text}\" isn't a field of this query." + (all.Count == 0 ? " Add a table first."
            : $" Its fields: {string.Join(", ", all.Take(80))}{(all.Count > 80 ? "…" : "")}. A lookup's table only offers its columns once the lookup is added."));
    }

    /// <summary>The SQL of a derived field, CASE expression or aggregate named by its alias (or written as its SQL), or null.</summary>
    public static string? Expression(QueryFile query, DbEngine engine, string text)
    {
        var t = text.Trim();
        foreach (var d in QueryEdits.All<DerivedFieldResult>(query).SelectMany(d => d.Fields))
        {
            var sql = Dialect.Derive(engine, d.SourceField, d.Derivation, d.Parameter);
            if (Same(d.Alias, t) || sql == t) return sql;
        }
        foreach (var c in QueryEdits.All<CaseWhenResult>(query).SelectMany(c => c.Expressions))
        {
            var sql = QueryEdits.CaseSql(c);
            if (Same(c.Alias, t) || sql == t) return sql;
        }
        foreach (var a in QueryEdits.All<GroupByResult>(query).SelectMany(g => g.Aggregates))
        {
            var sql = Aggregate(a);
            if (Same(a.Alias, t) || sql.Equals(t, StringComparison.OrdinalIgnoreCase)) return sql;
        }
        return null;
    }

    public static string Aggregate(AggregateField a) => a.Function switch
    {
        "COUNT(*)" => "COUNT(*)",
        "COUNT DISTINCT" => $"COUNT(DISTINCT {a.Field})",
        _ => $"{a.Function}({a.Field})"
    };

    private static bool Same(string? alias, string text) =>
        !string.IsNullOrWhiteSpace(alias) && (alias.Equals(text, StringComparison.OrdinalIgnoreCase) || alias.Equals(Catalog.Unquote(text), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Names(TableInfo t) => [t.FullName, t.Name];
}

/// <summary>Checks on text an agent puts into the SQL as it is (names, a derivation's parameter), not as a quoted value.</summary>
public static class Guard
{
    /// <summary>Refuses text that could end a statement or start a comment: names never need it.</summary>
    public static void Identifier(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{what} is needed.");
        Sql(text, what);
    }

    /// <summary>Refuses ; -- /* */ in SQL that goes in as written (values are quoted and never need this).</summary>
    public static void Sql(string? text, string what)
    {
        if (text is null) return;
        if (text.Contains(';') || text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal) || text.Contains("*/", StringComparison.Ordinal))
            throw new ArgumentException($"{what} can't contain ; -- /* or */ (\"{text}\"): it goes into the SQL as written.");
    }
}
