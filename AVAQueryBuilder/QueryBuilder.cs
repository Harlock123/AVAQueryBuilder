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
            sb.Append($" ON ");

            var joinConditions = lookup.JoinFieldsFromSource
                .Select(sf => $"{lookup.SourceTableName}.{sf} = {alias}.{lookup.JoinFieldInLookup}")
                .ToList();
            sb.Append(string.Join(" AND ", joinConditions));
        }

        return sb.ToString();
    }
}
