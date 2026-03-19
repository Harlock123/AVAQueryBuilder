using System.Collections.Generic;
using System.Linq;
using System.Text;
using AVASdCanvas.Models;

namespace AVAQueryBuilder;

public static class QueryBuilder
{
    private const int MaxLineWidth = 80;

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

        var derived = entityList
            .Where(e => e.Metadata is DerivedFieldResult)
            .Select(e => (DerivedFieldResult)e.Metadata!)
            .FirstOrDefault();

        var isDistinct = entityList.Any(e => e.Metadata is DistinctResult);

        var groupBy = entityList
            .Where(e => e.Metadata is GroupByResult)
            .Select(e => (GroupByResult)e.Metadata!)
            .FirstOrDefault();

        if (tableSources.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("SELECT ");

        // DISTINCT keyword
        if (isDistinct)
            sb.Append("DISTINCT ");

        // TOP N clause
        if (limiter != null)
            sb.Append($"TOP {limiter.TopCount} ");

        var allColumns = new List<string>();

        if (groupBy != null)
        {
            // GROUP BY mode: select grouped fields + aggregate expressions
            // Check derived fields for aliases on grouped expressions
            foreach (var gf in groupBy.GroupFields)
            {
                var colExpr = gf;
                if (derived != null)
                {
                    var matchingDerived = derived.Fields.FirstOrDefault(df =>
                        DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter) == gf);
                    if (matchingDerived != null && !string.IsNullOrWhiteSpace(matchingDerived.Alias))
                        colExpr += $" AS [{matchingDerived.Alias}]";
                }
                allColumns.Add(colExpr);
            }

            foreach (var agg in groupBy.Aggregates)
            {
                var expr = FormatAggregate(agg);
                if (!string.IsNullOrWhiteSpace(agg.Alias))
                    expr += $" AS [{agg.Alias}]";
                allColumns.Add(expr);
            }

            // Add derived fields not already included via group fields
            if (derived != null)
            {
                foreach (var df in derived.Fields)
                {
                    var expr = DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter);
                    if (!groupBy.GroupFields.Contains(expr))
                    {
                        if (!string.IsNullOrWhiteSpace(df.Alias))
                            expr += $" AS [{df.Alias}]";
                        allColumns.Add(expr);
                    }
                }
            }
        }
        else
        {
            // Normal mode: columns from table sources
            foreach (var table in tableSources)
            {
                var prefix = (tableSources.Count > 1 || lookups.Count > 0)
                    ? $"{table.TableName}."
                    : "";
                foreach (var col in table.SelectedColumns)
                {
                    var colExpr = $"{prefix}{col}";
                    if (table.ColumnAliases.TryGetValue(col, out var colAlias) && !string.IsNullOrWhiteSpace(colAlias))
                        colExpr += $" AS [{colAlias}]";
                    allColumns.Add(colExpr);
                }
            }

            // Columns from lookup joins
            foreach (var lookup in lookups)
            {
                var tableAlias = $"LOOKUP_{lookup.OrdinalValue}";
                foreach (var col in lookup.ReturnFields)
                {
                    var colExpr = $"{tableAlias}.{col}";
                    if (lookup.ColumnAliases.TryGetValue(col, out var colAlias) && !string.IsNullOrWhiteSpace(colAlias))
                        colExpr += $" AS [{colAlias}]";
                    allColumns.Add(colExpr);
                }
            }

            // Derived field expressions
            if (derived != null)
            {
                foreach (var df in derived.Fields)
                {
                    var expr = DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter);
                    if (!string.IsNullOrWhiteSpace(df.Alias))
                        expr += $" AS [{df.Alias}]";
                    allColumns.Add(expr);
                }
            }
        }

        AppendWrappedList(sb, allColumns, "       ");
        sb.AppendLine();

        // FROM clause
        sb.Append("FROM ");
        AppendWrappedList(sb, tableSources.Select(t => t.TableName).ToList(), "     ");

        // JOIN clauses for lookups
        foreach (var lookup in lookups)
        {
            var alias = $"LOOKUP_{lookup.OrdinalValue}";
            sb.AppendLine();
            var joinType = string.IsNullOrWhiteSpace(lookup.JoinType) ? "LEFT JOIN" : lookup.JoinType;
            sb.Append($"{joinType} {lookup.LookupTableName} AS {alias}");
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

            var combiner = $" {filter.Combiner} ";
            AppendWrappedList(sb, whereParts, "      ", combiner);
        }

        // GROUP BY clause
        if (groupBy != null && groupBy.GroupFields.Count > 0)
        {
            sb.AppendLine();
            sb.Append("GROUP BY ");
            AppendWrappedList(sb, groupBy.GroupFields, "         ");

            // HAVING clause
            if (groupBy.HavingConditions.Count > 0)
            {
                sb.AppendLine();
                sb.Append("HAVING ");
                var havingParts = groupBy.HavingConditions
                    .Select(h => $"{h.Expression} {h.Operator} {h.Value}")
                    .ToList();
                var havingCombiner = $" {groupBy.HavingCombiner} ";
                AppendWrappedList(sb, havingParts, "       ", havingCombiner);
            }
        }

        // ORDER BY clause
        if (sorting != null && sorting.Fields.Count > 0)
        {
            sb.AppendLine();
            sb.Append("ORDER BY ");
            var orderParts = sorting.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Field))
                .Select(f => $"{f.Field} {f.Direction}")
                .ToList();
            AppendWrappedList(sb, orderParts, "         ");
        }

        return sb.ToString();
    }

    private static string FormatAggregate(AggregateField agg)
    {
        return agg.Function switch
        {
            "COUNT(*)" => "COUNT(*)",
            "COUNT DISTINCT" => $"COUNT(DISTINCT {agg.Field})",
            _ => $"{agg.Function}({agg.Field})"
        };
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

    private static void AppendWrappedList(StringBuilder sb, List<string> items, string indent, string separator = ", ")
    {
        if (items.Count == 0) return;

        var lineLength = sb.Length - sb.ToString().LastIndexOf('\n') - 1;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var sep = i < items.Count - 1 ? separator : "";
            var chunk = item + sep;

            if (i > 0 && lineLength + chunk.Length > MaxLineWidth)
            {
                sb.AppendLine();
                sb.Append(indent);
                lineLength = indent.Length;
            }

            sb.Append(chunk);
            lineLength += chunk.Length;
        }
    }

    private static string FormatInValues(string value)
    {
        var items = value.Split(',').Select(v => $"'{v.Trim()}'");
        return string.Join(", ", items);
    }
}
