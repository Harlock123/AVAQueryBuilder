using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class AddGroupByDialog : Window
{
    private readonly ObservableCollection<ColumnItem> _groupFields = new();
    private readonly ObservableCollection<AggregateViewModel> _aggregates = new();
    private readonly ObservableCollection<HavingViewModel> _havingConditions = new();
    private List<string> _availableFields = new();

    public TableSourceResult SourceTable { get; set; } = null!;
    public List<ConnectedSourceResult> Lookups { get; set; } = new();
    public DerivedFieldResult? DerivedFields { get; set; }
    public GroupByResult? ExistingResult { get; set; }
    public GroupByResult? Result { get; private set; }

    public AddGroupByDialog()
    {
        InitializeComponent();
        lstGroupFields.ItemsSource = _groupFields;
        aggregateList.ItemsSource = _aggregates;
        havingList.ItemsSource = _havingConditions;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await BuildAvailableFieldsAsync();

        // Populate group fields checkboxes
        foreach (var field in _availableFields)
            _groupFields.Add(new ColumnItem { Name = field, IsSelected = false });

        if (ExistingResult != null)
            RehydrateFromExisting();
        else
            AddEmptyAggregate();

        UpdatePreview();
    }

    private async Task BuildAvailableFieldsAsync()
    {
        var fields = new List<string>();
        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            var baseColumns = await GetAllColumnsAsync(connStr, SourceTable.TableName);
            foreach (var col in baseColumns)
                fields.Add($"{SourceTable.TableName}.{col}");

            foreach (var lookup in Lookups)
            {
                var alias = $"LOOKUP_{lookup.OrdinalValue}";
                var lookupColumns = await GetAllColumnsAsync(connStr, lookup.LookupTableName);
                foreach (var col in lookupColumns)
                    fields.Add($"{alias}.{col}");
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error loading fields: {ex.Message}";
        }

        // Add derived field expressions
        if (DerivedFields != null)
        {
            foreach (var df in DerivedFields.Fields)
            {
                var expr = DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter);
                fields.Add(expr);
            }
        }

        _availableFields = fields;
    }

    private static async Task<List<string>> GetAllColumnsAsync(string connStr, string schemaAndTable)
    {
        var columns = new List<string>();
        var parts = schemaAndTable.Split('.', 2);
        if (parts.Length != 2) return columns;
        var schema = parts[0];
        var table = parts[1];

        await Task.Run(() =>
        {
            using var conn = new SqlConnection(connStr);
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table " +
                "ORDER BY ORDINAL_POSITION", conn);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(0));
        });

        return columns;
    }

    private void RehydrateFromExisting()
    {
        var existing = ExistingResult!;

        // Restore group field selections
        foreach (var gf in _groupFields)
            gf.IsSelected = existing.GroupFields.Contains(gf.Name);

        // Restore aggregates
        foreach (var agg in existing.Aggregates)
        {
            var vm = new AggregateViewModel
            {
                AvailableFields = _availableFields,
                SelectedFunction = agg.Function,
                SelectedField = agg.Field,
                Alias = agg.Alias
            };
            _aggregates.Add(vm);
        }

        // Restore HAVING
        if (existing.HavingConditions.Count > 0)
        {
            // Set combiner
            for (var i = 0; i < cboHavingCombiner.ItemCount; i++)
            {
                if (cboHavingCombiner.Items[i] is ComboBoxItem item &&
                    item.Content?.ToString() == existing.HavingCombiner)
                {
                    cboHavingCombiner.SelectedIndex = i;
                    break;
                }
            }

            var havingExpressions = BuildHavingExpressions();
            foreach (var hc in existing.HavingConditions)
            {
                var vm = new HavingViewModel
                {
                    AvailableExpressions = havingExpressions,
                    Expression = hc.Expression,
                    SelectedOperator = hc.Operator,
                    Value = hc.Value
                };
                _havingConditions.Add(vm);
            }
        }
    }

    private List<string> BuildHavingExpressions()
    {
        var expressions = new List<string>();
        foreach (var agg in _aggregates)
        {
            if (!string.IsNullOrWhiteSpace(agg.SelectedFunction))
                expressions.Add(agg.ToSqlExpression());
        }
        if (expressions.Count == 0)
            expressions.Add("COUNT(*)");
        return expressions;
    }

    private void AddEmptyAggregate()
    {
        var vm = new AggregateViewModel
        {
            AvailableFields = _availableFields,
            SelectedFunction = "COUNT(*)",
            SelectedField = _availableFields.FirstOrDefault() ?? string.Empty
        };
        _aggregates.Add(vm);
    }

    private void CmdAddAggregate_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new AggregateViewModel
        {
            AvailableFields = _availableFields,
            SelectedFunction = "COUNT",
            SelectedField = _availableFields.FirstOrDefault() ?? string.Empty
        };
        _aggregates.Add(vm);
    }

    private void CmdRemoveAggregate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AggregateViewModel vm)
        {
            _aggregates.Remove(vm);
            UpdatePreview();
        }
    }

    private void CmdAddHaving_Click(object? sender, RoutedEventArgs e)
    {
        var expressions = BuildHavingExpressions();
        var vm = new HavingViewModel
        {
            AvailableExpressions = expressions,
            Expression = expressions.FirstOrDefault() ?? "COUNT(*)"
        };
        _havingConditions.Add(vm);
    }

    private void CmdRemoveHaving_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HavingViewModel vm)
        {
            _havingConditions.Remove(vm);
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var groupFields = _groupFields.Where(f => f.IsSelected).Select(f => f.Name).ToList();
        var aggregates = _aggregates
            .Where(a => !string.IsNullOrWhiteSpace(a.SelectedFunction))
            .ToList();

        if (groupFields.Count == 0 && aggregates.Count == 0)
        {
            txtPreview.Text = "(select group fields and/or add aggregates)";
            return;
        }

        var selectParts = new List<string>();
        selectParts.AddRange(groupFields);
        foreach (var agg in aggregates)
        {
            var expr = agg.ToSqlExpression();
            if (!string.IsNullOrWhiteSpace(agg.Alias))
                expr += $" AS [{agg.Alias}]";
            selectParts.Add(expr);
        }

        var preview = "SELECT " + string.Join(", ", selectParts);

        if (groupFields.Count > 0)
            preview += "\nGROUP BY " + string.Join(", ", groupFields);

        var havingParts = _havingConditions
            .Where(h => !string.IsNullOrWhiteSpace(h.Expression) && !string.IsNullOrWhiteSpace(h.Value))
            .Select(h => $"{h.Expression} {h.SelectedOperator} {h.Value}")
            .ToList();

        if (havingParts.Count > 0)
        {
            var combiner = (cboHavingCombiner.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AND";
            preview += $"\nHAVING {string.Join($" {combiner} ", havingParts)}";
        }

        txtPreview.Text = preview;
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        var groupFields = _groupFields.Where(f => f.IsSelected).Select(f => f.Name).ToList();
        var validAggregates = _aggregates
            .Where(a => !string.IsNullOrWhiteSpace(a.SelectedFunction))
            .ToList();

        if (groupFields.Count == 0 && validAggregates.Count == 0)
        {
            txtStatus.Text = "Select at least one group field or add an aggregate.";
            return;
        }

        // Validate aliases
        foreach (var agg in validAggregates)
        {
            if (string.IsNullOrWhiteSpace(agg.Alias))
            {
                txtStatus.Text = "Each aggregate must have an alias.";
                return;
            }
        }

        // Validate aggregate fields (non COUNT(*) must have a field)
        foreach (var agg in validAggregates)
        {
            if (agg.SelectedFunction != "COUNT(*)" && string.IsNullOrWhiteSpace(agg.SelectedField))
            {
                txtStatus.Text = $"'{agg.SelectedFunction}' requires a field selection.";
                return;
            }
        }

        var combiner = (cboHavingCombiner.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AND";

        var havingConditions = _havingConditions
            .Where(h => !string.IsNullOrWhiteSpace(h.Expression) && !string.IsNullOrWhiteSpace(h.Value))
            .Select(h => new HavingCondition
            {
                Expression = h.Expression,
                Operator = h.SelectedOperator,
                Value = h.Value
            }).ToList();

        Result = new GroupByResult
        {
            GroupFields = groupFields,
            Aggregates = validAggregates.Select(a => new AggregateField
            {
                Function = a.SelectedFunction,
                Field = a.SelectedField,
                Alias = a.Alias
            }).ToList(),
            HavingConditions = havingConditions,
            HavingCombiner = combiner
        };

        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}
