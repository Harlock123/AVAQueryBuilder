using System.Collections.Generic;
using System.Linq;
using System.Text;
using AVASdCanvas.Models;

namespace AVAQueryBuilder;

public static class QueryBuilder
{
    private const int MaxLineWidth = 80;

    public static string BuildQuery(IEnumerable<GraphicEntity> entities) =>
        BuildQuery(entities.Select(e => e.Metadata), AppState.Engine);

    /// <summary>
    /// The SELECT for the canvas entities' metadata (<see cref="TableSourceResult"/> and the rest), in
    /// <paramref name="engine"/>'s dialect.
    /// </summary>
    public static string BuildQuery(IEnumerable<object?> metadata, DbEngine engine)
    {
        var entityList = metadata.ToList();

        var tableSources = entityList.OfType<TableSourceResult>().ToList();

        var lookups = entityList.OfType<ConnectedSourceResult>()
            .OrderBy(l => l.OrdinalValue)
            .ToList();

        var limiter = entityList.OfType<LimiterResult>().FirstOrDefault();

        var filters = entityList.OfType<FilterResult>().ToList();

        var sorting = entityList.OfType<SortingResult>().FirstOrDefault();

        var derived = entityList.OfType<DerivedFieldResult>().FirstOrDefault();

        var isDistinct = entityList.OfType<DistinctResult>().Any();

        var caseWhen = entityList.OfType<CaseWhenResult>().FirstOrDefault();

        var groupBy = entityList.OfType<GroupByResult>().FirstOrDefault();

        if (tableSources.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("SELECT ");

        // DISTINCT keyword
        if (isDistinct)
            sb.Append("DISTINCT ");

        // TOP N clause (SQL Server); the others end the query with LIMIT N
        if (limiter != null && Dialect.LimitsWithTop(engine))
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
                        DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter, engine) == gf);
                    if (matchingDerived != null && !string.IsNullOrWhiteSpace(matchingDerived.Alias))
                        colExpr += Dialect.As(engine, matchingDerived.Alias);
                }
                if (caseWhen != null)
                {
                    var matchingCase = caseWhen.Expressions.FirstOrDefault(ce =>
                        FormatCaseExpressionNoAlias(ce) == gf);
                    if (matchingCase != null && !string.IsNullOrWhiteSpace(matchingCase.Alias))
                        colExpr += Dialect.As(engine, matchingCase.Alias);
                }
                allColumns.Add(colExpr);
            }

            foreach (var agg in groupBy.Aggregates)
            {
                var expr = FormatAggregate(agg);
                if (!string.IsNullOrWhiteSpace(agg.Alias))
                    expr += Dialect.As(engine, agg.Alias);
                allColumns.Add(expr);
            }

            // Add derived fields not already included via group fields
            if (derived != null)
            {
                foreach (var df in derived.Fields)
                {
                    var expr = DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter, engine);
                    if (!groupBy.GroupFields.Contains(expr))
                    {
                        if (!string.IsNullOrWhiteSpace(df.Alias))
                            expr += Dialect.As(engine, df.Alias);
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
                        colExpr += Dialect.As(engine, colAlias);
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
                        colExpr += Dialect.As(engine, colAlias);
                    allColumns.Add(colExpr);
                }
            }

            // Derived field expressions
            if (derived != null)
            {
                foreach (var df in derived.Fields)
                {
                    var expr = DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter, engine);
                    if (!string.IsNullOrWhiteSpace(df.Alias))
                        expr += Dialect.As(engine, df.Alias);
                    allColumns.Add(expr);
                }
            }
        }

        // CASE/WHEN expressions (skip if already included via group fields)
        if (caseWhen != null)
        {
            foreach (var expr in caseWhen.Expressions)
            {
                var caseSqlNoAlias = FormatCaseExpressionNoAlias(expr);
                // Check if this CASE expression is already in allColumns (via group fields)
                if (allColumns.Any(c => c.StartsWith(caseSqlNoAlias)))
                    continue;
                var caseSql = FormatCaseExpression(expr, engine);
                allColumns.Add(caseSql);
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

        // WHERE clause — multiple filter entities, each wrapped in parentheses, AND'd together
        var activeFilters = filters.Where(f => f.Conditions.Count > 0).ToList();
        if (activeFilters.Count > 0)
        {
            sb.AppendLine();
            sb.Append("WHERE ");

            var filterGroups = new List<string>();
            foreach (var filter in activeFilters)
            {
                var whereParts = new List<string>();
                foreach (var c in filter.Conditions)
                {
                    var clause = FormatCondition(c, engine);
                    if (clause != null)
                        whereParts.Add(clause);
                }

                if (whereParts.Count == 0) continue;

                var combiner = $" {filter.Combiner} ";
                var group = string.Join(combiner, whereParts);

                // Wrap in parentheses if multiple filters exist
                if (activeFilters.Count > 1)
                    group = $"({group})";

                filterGroups.Add(group);
            }

            AppendWrappedList(sb, filterGroups, "      ", " AND ");
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

        if (limiter != null && !Dialect.LimitsWithTop(engine))
        {
            sb.AppendLine();
            sb.Append($"LIMIT {limiter.TopCount}");
        }

        return sb.ToString();
    }

    private static string FormatCaseExpressionNoAlias(CaseExpression expr)
    {
        var sb = new System.Text.StringBuilder("CASE");
        foreach (var wt in expr.Conditions)
        {
            if (wt.Operator is "IS NULL")
                sb.Append($" WHEN {expr.Field} IS NULL THEN '{wt.ThenValue}'");
            else if (wt.Operator is "IS NOT NULL")
                sb.Append($" WHEN {expr.Field} IS NOT NULL THEN '{wt.ThenValue}'");
            else
                sb.Append($" WHEN {expr.Field} {wt.Operator} '{wt.WhenValue}' THEN '{wt.ThenValue}'");
        }
        if (!string.IsNullOrWhiteSpace(expr.ElseValue))
            sb.Append($" ELSE '{expr.ElseValue}'");
        sb.Append(" END");
        return sb.ToString();
    }

    private static string FormatCaseExpression(CaseExpression expr, DbEngine engine)
    {
        var sb = new System.Text.StringBuilder("CASE");
        foreach (var wt in expr.Conditions)
        {
            if (wt.Operator is "IS NULL")
                sb.Append($" WHEN {expr.Field} IS NULL THEN '{wt.ThenValue}'");
            else if (wt.Operator is "IS NOT NULL")
                sb.Append($" WHEN {expr.Field} IS NOT NULL THEN '{wt.ThenValue}'");
            else
                sb.Append($" WHEN {expr.Field} {wt.Operator} '{wt.WhenValue}' THEN '{wt.ThenValue}'");
        }
        if (!string.IsNullOrWhiteSpace(expr.ElseValue))
            sb.Append($" ELSE '{expr.ElseValue}'");
        sb.Append(" END");
        if (!string.IsNullOrWhiteSpace(expr.Alias))
            sb.Append(Dialect.As(engine, expr.Alias));
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

    private static string? FormatCondition(FilterCondition c, DbEngine engine)
    {
        var field = c.Field;
        if (string.IsNullOrWhiteSpace(field)) return null;

        var castAs = c.CastAs;
        if (!string.IsNullOrWhiteSpace(castAs) && castAs != "(none)")
            field = Dialect.Cast(engine, field, castAs);

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
