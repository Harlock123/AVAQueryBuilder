using System.Collections.Generic;
using System.Linq;
using System.Text;
using AVASdCanvas.Models;

namespace AVAQueryBuilder;

public static class QueryBuilder
{
    public static string BuildQuery(IEnumerable<GraphicEntity> entities)
    {
        var entityList = entities.ToList();

        var tableSources = entityList
            .Where(e => e.Metadata is TableSourceResult)
            .Select(e => (TableSourceResult)e.Metadata!)
            .ToList();

        var lookups = entityList
            .Where(e => e.Metadata is ConnectedSourceResult)
            .Select(e => (ConnectedSourceResult)e.Metadata!)
            .OrderBy(l => l.OrdinalValue)
            .ToList();

        var limiter = entityList
            .Where(e => e.Metadata is LimiterResult)
            .Select(e => (LimiterResult)e.Metadata!)
            .FirstOrDefault();

        var filter = entityList
            .Where(e => e.Metadata is FilterResult)
            .Select(e => (FilterResult)e.Metadata!)
            .FirstOrDefault();

        var sorting = entityList
            .Where(e => e.Metadata is SortingResult)
            .Select(e => (SortingResult)e.Metadata!)
            .FirstOrDefault();

        if (tableSources.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("SELECT ");

        // TOP N clause
        if (limiter != null)
            sb.Append($"TOP {limiter.TopCount} ");

        // Columns from table sources
        var allColumns = new List<string>();
        foreach (var table in tableSources)
        {
            var prefix = (tableSources.Count > 1 || lookups.Count > 0)
                ? $"{table.TableName}."
                : "";
            allColumns.AddRange(table.SelectedColumns.Select(col => $"{prefix}{col}"));
        }

        // Columns from lookup joins
        foreach (var lookup in lookups)
        {
            var alias = $"LOOKUP_{lookup.OrdinalValue}";
            allColumns.AddRange(lookup.ReturnFields.Select(col => $"{alias}.{col}"));
        }

        sb.AppendLine(string.Join(", ", allColumns));

        // FROM clause
        sb.Append("FROM ");
        sb.Append(string.Join(", ", tableSources.Select(t => t.TableName)));

        // JOIN clauses for lookups
        foreach (var lookup in lookups)
        {
            var alias = $"LOOKUP_{lookup.OrdinalValue}";
            sb.AppendLine();
            sb.Append($"LEFT JOIN {lookup.LookupTableName} AS {alias}");
            sb.Append(" ON ");

            var joinConditions = lookup.JoinFieldsFromSource
                .Select(sf => $"{lookup.SourceTableName}.{sf} = {alias}.{lookup.JoinFieldInLookup}")
                .ToList();
            sb.Append(string.Join(" AND ", joinConditions));
        }

        // WHERE clause
        if (filter != null && filter.Conditions.Count > 0)
        {
            sb.AppendLine();
            sb.Append("WHERE ");

            var whereParts = new List<string>();
            foreach (var c in filter.Conditions)
            {
                var clause = FormatCondition(c);
                if (clause != null)
                    whereParts.Add(clause);
            }

            sb.Append(string.Join($" {filter.Combiner} ", whereParts));
        }

        // ORDER BY clause
        if (sorting != null && sorting.Fields.Count > 0)
        {
            sb.AppendLine();
            sb.Append("ORDER BY ");
            var orderParts = sorting.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Field))
                .Select(f => $"{f.Field} {f.Direction}");
            sb.Append(string.Join(", ", orderParts));
        }

        return sb.ToString();
    }

    private static string? FormatCondition(FilterCondition c)
    {
        var field = c.Field;
        if (string.IsNullOrWhiteSpace(field)) return null;

        var castAs = c.CastAs;
        if (!string.IsNullOrWhiteSpace(castAs) && castAs != "(none)")
        {
            var castType = castAs == "VARCHAR" ? "VARCHAR(MAX)" : castAs;
            field = $"CAST({field} AS {castType})";
        }

        return c.Operator switch
        {
            "IS NULL" => $"{field} IS NULL",
            "IS NOT NULL" => $"{field} IS NOT NULL",
            "IS EMPTY" => $"{field} = ''",
            "IS NOT EMPTY" => $"{field} != ''",
            "BETWEEN" => $"{field} BETWEEN '{c.Value}' AND '{c.Value2}'",
            "IN" => $"{field} IN ({FormatInValues(c.Value)})",
            "NOT IN" => $"{field} NOT IN ({FormatInValues(c.Value)})",
            "LIKE" => $"{field} LIKE '{c.Value}'",
            "NOT LIKE" => $"{field} NOT LIKE '{c.Value}'",
            _ => $"{field} {c.Operator} '{c.Value}'"
        };
    }

    private static string FormatInValues(string value)
    {
        var items = value.Split(',').Select(v => $"'{v.Trim()}'");
        return string.Join(", ", items);
    }
}
