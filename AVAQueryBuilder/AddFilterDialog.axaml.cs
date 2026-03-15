using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class AddFilterDialog : Window
{
    private readonly ObservableCollection<FilterConditionViewModel> _conditions = new();
    private List<string> _availableFields = new();

    public TableSourceResult SourceTable { get; set; } = null!;
    public List<ConnectedSourceResult> Lookups { get; set; } = new();
    public FilterResult? ExistingResult { get; set; }
    public FilterResult? Result { get; private set; }

    public AddFilterDialog()
    {
        InitializeComponent();
        conditionsList.ItemsSource = _conditions;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await BuildAvailableFieldsAsync();

        if (ExistingResult != null)
            RehydrateFromExisting();
        else
            AddEmptyCondition();

        UpdatePreview();
    }

    private async Task BuildAvailableFieldsAsync()
    {
        var fields = new List<string>();
        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            // Get ALL columns from the base table (not just selected ones)
            var baseTableColumns = await GetAllColumnsAsync(connStr, SourceTable.TableName);
            foreach (var col in baseTableColumns)
                fields.Add($"{SourceTable.TableName}.{col}");

            // Get ALL columns from each lookup table, using their aliases
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

        cboCombiner.SelectedIndex = existing.Combiner == "OR" ? 1 : 0;

        foreach (var cond in existing.Conditions)
        {
            var vm = new FilterConditionViewModel
            {
                AvailableFields = _availableFields,
                SelectedField = cond.Field,
                SelectedCastAs = cond.CastAs ?? "(none)",
                SelectedOperator = cond.Operator,
                Value = cond.Value,
                Value2 = cond.Value2
            };
            _conditions.Add(vm);
        }
    }

    private void AddEmptyCondition()
    {
        var vm = new FilterConditionViewModel
        {
            AvailableFields = _availableFields,
            SelectedField = _availableFields.FirstOrDefault() ?? string.Empty
        };
        _conditions.Add(vm);
    }

    private void CmdAddCondition_Click(object? sender, RoutedEventArgs e)
    {
        AddEmptyCondition();
    }

    private void CmdRemoveCondition_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FilterConditionViewModel vm)
        {
            _conditions.Remove(vm);
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var validConditions = _conditions
            .Where(c => !string.IsNullOrWhiteSpace(c.SelectedField))
            .ToList();

        if (validConditions.Count == 0)
        {
            txtPreview.Text = "(no conditions)";
            return;
        }

        var combiner = (cboCombiner.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AND";
        var parts = new List<string>();

        foreach (var c in validConditions)
        {
            var clause = FormatCondition(c);
            if (clause != null)
                parts.Add(clause);
        }

        txtPreview.Text = parts.Count > 0
            ? "WHERE " + string.Join($" {combiner} ", parts)
            : "(no valid conditions)";
    }

    private static string? FormatCondition(FilterConditionViewModel c)
    {
        var field = c.SelectedField;
        if (string.IsNullOrWhiteSpace(field)) return null;

        var castAs = c.SelectedCastAs;
        if (!string.IsNullOrWhiteSpace(castAs) && castAs != "(none)")
        {
            var castType = castAs == "VARCHAR" ? "VARCHAR(MAX)" : castAs;
            field = $"CAST({field} AS {castType})";
        }

        return c.SelectedOperator switch
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
            _ => $"{field} {c.SelectedOperator} '{c.Value}'"
        };
    }

    private static string FormatInValues(string value)
    {
        var items = value.Split(',').Select(v => $"'{v.Trim()}'");
        return string.Join(", ", items);
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        var validConditions = _conditions
            .Where(c => !string.IsNullOrWhiteSpace(c.SelectedField))
            .ToList();

        if (validConditions.Count == 0)
        {
            txtStatus.Text = "Add at least one condition.";
            return;
        }

        // Validate that non-null operators have values
        foreach (var c in validConditions)
        {
            if (c.SelectedOperator is not ("IS NULL" or "IS NOT NULL" or "IS EMPTY" or "IS NOT EMPTY") &&
                string.IsNullOrWhiteSpace(c.Value))
            {
                txtStatus.Text = $"Enter a value for {c.SelectedField}.";
                return;
            }

            if (c.SelectedOperator == "BETWEEN" && string.IsNullOrWhiteSpace(c.Value2))
            {
                txtStatus.Text = $"Enter a second value for {c.SelectedField} BETWEEN.";
                return;
            }
        }

        var combiner = (cboCombiner.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AND";

        Result = new FilterResult
        {
            Combiner = combiner,
            Conditions = validConditions.Select(c => new FilterCondition
            {
                Field = c.SelectedField,
                CastAs = c.SelectedCastAs,
                Operator = c.SelectedOperator,
                Value = c.Value,
                Value2 = c.Value2
            }).ToList()
        };

        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}
