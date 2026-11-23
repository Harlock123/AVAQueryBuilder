using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAQueryBuilder;

/// <summary>
/// The edits that change a query, on its saved form (<see cref="QueryFile"/>): adding a table, a lookup, a filter and
/// the rest, each placed on the diagram and joined to the base table as the window's buttons always placed them. The
/// window's buttons and the AI tools both make their changes here, so a query an agent builds looks the same as one a
/// person builds, and both go through the same undo history (<see cref="QueryHistory"/>).
/// </summary>
public static class QueryEdits
{
    public const double EntityWidth = 160, EntityHeight = 40, RowStep = 60, Gap = 60;
    public const double FirstX = 40, FirstY = 40;

    /// <summary>The kinds of entity, as a .qry file names them.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["Table", "Lookup", "Derived", "CaseWhen", "GroupBy", "Limiter", "Distinct", "Filter", "Sorting"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>A deep copy (the metadata objects too), so a snapshot can't change under the history.</summary>
    public static QueryFile Clone(QueryFile file) => JsonSerializer.Deserialize<QueryFile>(JsonSerializer.Serialize(file, Json))!;

    public static string ToJson(QueryFile file) => JsonSerializer.Serialize(file, Json);

    public static QueryFile FromJson(string json) =>
        JsonSerializer.Deserialize<QueryFile>(json) ?? throw new JsonException("The file is empty.");

    /// <summary>The metadata object of an entity (<see cref="TableSourceResult"/> and the rest).</summary>
    public static object? Metadata(EntityState e) => e.EntityType switch
    {
        "Table" => e.TableSource,
        "Lookup" => e.ConnectedSource,
        "Limiter" => e.Limiter,
        "Filter" => e.Filter,
        "Sorting" => e.Sorting,
        "Derived" => e.Derived,
        "Distinct" => e.Distinct,
        "CaseWhen" => e.CaseWhen,
        "GroupBy" => e.GroupBy,
        _ => null
    };

    /// <summary>An entity's kind, as a .qry file names it, from its metadata.</summary>
    public static string KindOf(object metadata) => metadata switch
    {
        TableSourceResult => "Table",
        ConnectedSourceResult => "Lookup",
        LimiterResult => "Limiter",
        FilterResult => "Filter",
        SortingResult => "Sorting",
        DerivedFieldResult => "Derived",
        DistinctResult => "Distinct",
        CaseWhenResult => "CaseWhen",
        GroupByResult => "GroupBy",
        _ => throw new ArgumentException($"{metadata.GetType().Name} isn't a query entity.")
    };

    /// <summary>Puts <paramref name="metadata"/> on an entity, clearing what it held before.</summary>
    public static void SetMetadata(EntityState e, object metadata)
    {
        e.TableSource = null; e.ConnectedSource = null; e.Limiter = null; e.Filter = null; e.Sorting = null;
        e.Derived = null; e.Distinct = null; e.CaseWhen = null; e.GroupBy = null;
        e.EntityType = KindOf(metadata);
        switch (metadata)
        {
            case TableSourceResult t: e.TableSource = t; break;
            case ConnectedSourceResult c: e.ConnectedSource = c; break;
            case LimiterResult l: e.Limiter = l; break;
            case FilterResult f: e.Filter = f; break;
            case SortingResult s: e.Sorting = s; break;
            case DerivedFieldResult d: e.Derived = d; break;
            case DistinctResult d: e.Distinct = d; break;
            case CaseWhenResult c: e.CaseWhen = c; break;
            case GroupByResult g: e.GroupBy = g; break;
        }
    }

    /// <summary>The label the diagram shows for an entity.</summary>
    public static string Label(object metadata, int filterNumber = 1) => metadata switch
    {
        TableSourceResult t => t.TableName,
        ConnectedSourceResult c => c.LookupTableName,
        LimiterResult l => $"TOP {l.TopCount}",
        FilterResult f => $"WHERE #{filterNumber} ({f.Conditions.Count})",
        SortingResult s => $"ORDER BY ({s.Fields.Count})",
        DerivedFieldResult d => $"DERIVED ({d.Fields.Count})",
        DistinctResult => "DISTINCT",
        CaseWhenResult c => $"CASE/WHEN ({c.Expressions.Count})",
        GroupByResult g => $"GROUP BY ({g.GroupFields.Count}+{g.Aggregates.Count})",
        _ => ""
    };

    /// <summary>Every entity's metadata, in order, for <see cref="QueryBuilder.BuildQuery(IEnumerable{object?}, DbEngine)"/>.</summary>
    public static IEnumerable<object?> AllMetadata(QueryFile file) => file.Entities.Select(Metadata);

    public static string Sql(QueryFile file, DbEngine engine) => QueryBuilder.BuildQuery(AllMetadata(file), engine);

    public static EntityState? Find(QueryFile file, string id) =>
        file.Entities.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public static EntityState? First(QueryFile file, string kind) => file.Entities.FirstOrDefault(e => e.EntityType == kind);

    public static EntityState? BaseTable(QueryFile file) => First(file, "Table");

    public static IEnumerable<T> All<T>(QueryFile file) where T : class => file.Entities.Select(Metadata).OfType<T>();

    /// <summary>A fresh entity id: e1, e2… (files from before ids were short keep theirs).</summary>
    public static string NewId(QueryFile file)
    {
        var next = file.Entities.Select(e => e.Id.Length > 1 && e.Id[0] == 'e' && int.TryParse(e.Id.AsSpan(1), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;
        return "e" + next;
    }

    private static EntityState Place(QueryFile file, object metadata, double x, double y, double width = EntityWidth, double height = EntityHeight)
    {
        var e = new EntityState { Id = NewId(file), X = x, Y = y, Width = width, Height = height };
        SetMetadata(e, metadata);
        e.Label = Label(metadata, file.Entities.Count(x2 => x2.EntityType == "Filter") + 1);
        file.Entities.Add(e);
        return e;
    }

    private static void Join(QueryFile file, EntityState from, EntityState to) =>
        file.Connectors.Add(new ConnectorState { SourceEntityId = from.Id, TargetEntityId = to.Id, SourceEndpoint = "RoundDot", TargetEndpoint = "PointedArrow" });

    /// <summary>
    /// The first free row in the column to the right of the base table, from the top down, so entities never land on
    /// one another whatever order they're added in (<paramref name="countKinds"/> is kept for the callers' record of
    /// where the window used to put each kind).
    /// </summary>
    private static (double X, double Y) RightSlot(QueryFile file, EntityState table, params string[] countKinds)
    {
        _ = countKinds;
        var x = table.X + table.Width + Gap;
        var taken = file.Entities.Where(e => e != table && e.X < x + EntityWidth && e.X + e.Width > x).Select(e => (e.Y, e.Y + e.Height)).ToList();
        for (var row = 0; ; row++)
        {
            var y = table.Y + row * RowStep;
            if (!taken.Any(t => y < t.Item2 && y + EntityHeight > t.Item1)) return (x, y);
        }
    }

    private static EntityState RequireTable(QueryFile file) =>
        BaseTable(file) ?? throw new InvalidOperationException("Add a table first: every query starts from a base table.");

    /// <summary>Removes an entity and the lines joined to it.</summary>
    public static void Remove(QueryFile file, EntityState entity)
    {
        file.Connectors.RemoveAll(c => c.SourceEntityId == entity.Id || c.TargetEntityId == entity.Id);
        file.Entities.Remove(entity);
    }

    /// <summary>
    /// Deletes an entity as the window's Delete does: the base table takes everything with it (the rest of the query
    /// hangs off it); anything else goes alone, and the filters are numbered again.
    /// </summary>
    public static void Delete(QueryFile file, EntityState entity)
    {
        if (ReferenceEquals(entity, BaseTable(file)))
        {
            Clear(file);
            return;
        }
        Remove(file, entity);
        Relabel(file);
    }

    public static void Clear(QueryFile file)
    {
        file.Entities.Clear();
        file.Connectors.Clear();
        file.LookupOrdinal = 0;
    }

    /// <summary>Every entity's label from its metadata (filters numbered in order).</summary>
    public static void Relabel(QueryFile file)
    {
        var n = 0;
        foreach (var e in file.Entities)
            if (Metadata(e) is { } m)
                e.Label = Label(m, m is FilterResult ? ++n : 1);
    }

    /// <summary>A table (the first is the base table, top left; another is listed beside it in FROM).</summary>
    public static EntityState AddTable(QueryFile file, TableSourceResult table)
    {
        var tables = file.Entities.Where(e => e.EntityType == "Table").ToList();
        var y = tables.Count == 0 ? FirstY : tables.Max(t => t.Y + t.Height) + 20;
        return Place(file, table, FirstX, y);
    }

    /// <summary>A lookup join from the base table, LOOKUP_n with the next n.</summary>
    public static EntityState AddLookup(QueryFile file, ConnectedSourceResult lookup)
    {
        var table = RequireTable(file);
        lookup.OrdinalValue = ++file.LookupOrdinal;
        var (x, y) = RightSlot(file, table, "Lookup");
        var e = Place(file, lookup, x, y);
        Join(file, e, table);
        return e;
    }

    /// <summary>One more WHERE group (each filter's conditions are bracketed and ANDed with the others').</summary>
    public static EntityState AddFilter(QueryFile file, FilterResult filter)
    {
        var table = RequireTable(file);
        var (x, y) = RightSlot(file, table, "Lookup", "Limiter", "Filter", "Sorting", "Derived", "CaseWhen", "GroupBy");
        var e = Place(file, filter, x, y);
        Join(file, table, e);
        return e;
    }

    /// <summary>
    /// Sets the query's one entity of a kind (derived fields, CASE WHEN, group by, sorting, the row limit, DISTINCT):
    /// replaces it in place if there is one, else adds it where the window puts it. Null removes it.
    /// </summary>
    public static EntityState? SetSingle(QueryFile file, string kind, object? metadata)
    {
        var existing = First(file, kind);
        if (metadata == null)
        {
            if (existing != null) Remove(file, existing);
            return null;
        }
        if (existing != null)
        {
            SetMetadata(existing, metadata);
            existing.Label = Label(metadata);
            return existing;
        }
        var table = RequireTable(file);
        EntityState e;
        switch (kind)
        {
            case "Derived":
            {
                var (x, y) = RightSlot(file, table, "Lookup", "Limiter", "Filter", "Sorting");
                e = Place(file, metadata, x, y);
                Join(file, e, table);
                break;
            }
            case "CaseWhen":
            {
                var (x, y) = RightSlot(file, table, "Lookup", "Limiter", "Filter", "Sorting", "Derived", "GroupBy");
                e = Place(file, metadata, x, y);
                Join(file, e, table);
                break;
            }
            case "GroupBy":
            {
                var (x, y) = RightSlot(file, table, "Lookup", "Limiter", "Filter", "Sorting", "Derived");
                e = Place(file, metadata, x, y);
                Join(file, table, e);
                break;
            }
            case "Sorting":
            {
                var (x, y) = RightSlot(file, table, "Lookup", "Limiter", "Filter");
                e = Place(file, metadata, x, y);
                Join(file, table, e);
                break;
            }
            case "Limiter":
            {
                var (x, y) = RightSlot(file, table, "Lookup");
                e = Place(file, metadata, x, y);
                Join(file, table, e);
                break;
            }
            case "Distinct":
                e = Place(file, metadata, table.X, table.Y + table.Height + 20, 120, 35);
                Join(file, e, table);
                break;
            default:
                throw new ArgumentException($"{kind} isn't a kind there's only one of.");
        }
        return e;
    }

    /// <summary>Replaces an entity's settings (an edit), keeping its place on the diagram.</summary>
    public static void Replace(QueryFile file, EntityState entity, object metadata)
    {
        if (KindOf(metadata) != entity.EntityType)
            throw new ArgumentException($"{entity.Id} is a {entity.EntityType}, not a {KindOf(metadata)}.");
        if (metadata is ConnectedSourceResult c && entity.ConnectedSource is { } was) c.OrdinalValue = was.OrdinalValue;
        SetMetadata(entity, metadata);
        Relabel(file);
    }

    /// <summary>The SQL of a CASE expression without its alias, as group-by and sorting fields refer to it.</summary>
    public static string CaseSql(CaseExpression expr) => QueryBuilder.CaseExpressionSql(expr);
}

/// <summary>
/// One undo history for a query, shared by the person's edits and AI agents': each step is the whole query after it
/// (a .qry snapshot, which is small), with a name for menus and the timeline. Undo and redo hand back the query to
/// show. The connection isn't part of a step, so undo never reconnects anything.
/// </summary>
public sealed class QueryHistory
{
    private readonly List<(string Json, string Label)> _states = [];
    private int _index;
    private int _saved;

    public QueryHistory(QueryFile start) => Reset(start);

    /// <summary>Raised after every change to the history (a step, undo, redo, reset).</summary>
    public event Action? Changed;

    public int Index => _index;

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _states.Count - 1;

    /// <summary>What Undo would take back, or null.</summary>
    public string? UndoLabel => CanUndo ? _states[_index].Label : null;

    public string? RedoLabel => CanRedo ? _states[_index + 1].Label : null;

    /// <summary>Whether the query has changed since it was opened or saved.</summary>
    public bool IsDirty => _index != _saved;

    /// <summary>The names of the steps that can be undone, most recent first.</summary>
    public IReadOnlyList<string> Steps => _states.Take(_index + 1).Skip(1).Select(s => s.Label).Reverse().ToList();

    /// <summary>The query now, as a fresh copy.</summary>
    public QueryFile Current => QueryEdits.FromJson(_states[_index].Json);

    /// <summary>Starts again from <paramref name="start"/> (a new or opened query): nothing to undo, not changed.</summary>
    public void Reset(QueryFile start)
    {
        _states.Clear();
        _states.Add((Snapshot(start), "Start"));
        _index = 0;
        _saved = 0;
        Changed?.Invoke();
    }

    /// <summary>Records <paramref name="after"/> as one step named <paramref name="label"/>; false if nothing changed.</summary>
    public bool Commit(QueryFile after, string label)
    {
        var json = Snapshot(after);
        if (json == _states[_index].Json) return false;
        _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        if (_saved > _index) _saved = -1;
        _states.Add((json, label));
        _index++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Records the query's layout (entities moved by hand) without a step of its own: it rides on the step it's in.</summary>
    public void Amend(QueryFile now)
    {
        _states[_index] = (Snapshot(now), _states[_index].Label);
    }

    public QueryFile? Undo()
    {
        if (!CanUndo) return null;
        _index--;
        Changed?.Invoke();
        return Current;
    }

    public QueryFile? Redo()
    {
        if (!CanRedo) return null;
        _index++;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Folds the last <paramref name="steps"/> steps into one named <paramref name="label"/> (a batch).</summary>
    public void Squash(int steps, string label)
    {
        if (steps < 1 || steps > _index) return;
        var last = _states[_index].Json;
        var start = _index - steps + 1;
        _states.RemoveRange(start, _states.Count - start);
        _states.Add((last, label));
        _index = start;
        if (_saved > _index) _saved = -1;
        Changed?.Invoke();
    }

    /// <summary>Drops the steps that could be redone (after a failed batch is undone).</summary>
    public void ForgetRedo()
    {
        _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        if (_saved > _index) _saved = -1;
        Changed?.Invoke();
    }

    public void MarkSaved()
    {
        _saved = _index;
        Changed?.Invoke();
    }

    /// <summary>The query as a step keeps it: without the connection string, which isn't undone.</summary>
    private static string Snapshot(QueryFile file)
    {
        var copy = QueryEdits.Clone(file);
        copy.ConnectionString = "";
        return QueryEdits.ToJson(copy);
    }
}
