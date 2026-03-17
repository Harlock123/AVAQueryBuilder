using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class AddDerivedDialog : Window
{
    private readonly ObservableCollection<DerivedFieldViewModel> _derivedFields = new();
    private List<string> _availableFields = new();

    public TableSourceResult SourceTable { get; set; } = null!;
    public List<ConnectedSourceResult> Lookups { get; set; } = new();
    public DerivedFieldResult? ExistingResult { get; set; }
    public DerivedFieldResult? Result { get; private set; }

    public AddDerivedDialog()
    {
        InitializeComponent();
        derivedList.ItemsSource = _derivedFields;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await BuildAvailableFieldsAsync();

        if (ExistingResult != null)
            RehydrateFromExisting();
        else
            AddEmptyRow();

        UpdatePreview();
    }

    private async Task BuildAvailableFieldsAsync()
    {
        var fields = new List<string>();
        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            var baseTableColumns = await GetAllColumnsAsync(connStr, SourceTable.TableName);
            foreach (var col in baseTableColumns)
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
        foreach (var df in ExistingResult!.Fields)
        {
            var vm = new DerivedFieldViewModel
            {
                AvailableFields = _availableFields,
                SelectedField = df.SourceField,
                SelectedDerivation = df.Derivation,
                Parameter = df.Parameter,
                Alias = df.Alias
            };
            _derivedFields.Add(vm);
        }
    }

    private void AddEmptyRow()
    {
        var vm = new DerivedFieldViewModel
        {
            AvailableFields = _availableFields,
            SelectedField = _availableFields.FirstOrDefault() ?? string.Empty,
            SelectedDerivation = DerivedFieldViewModel.Derivations.FirstOrDefault() ?? string.Empty
        };
        _derivedFields.Add(vm);
    }

    private void CmdAddDerived_Click(object? sender, RoutedEventArgs e)
    {
        AddEmptyRow();
    }

    private void CmdRemoveDerived_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DerivedFieldViewModel vm)
        {
            _derivedFields.Remove(vm);
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var parts = new List<string>();

        foreach (var df in _derivedFields)
        {
            if (string.IsNullOrWhiteSpace(df.SelectedField) ||
                string.IsNullOrWhiteSpace(df.SelectedDerivation))
                continue;

            var expr = DerivedFieldViewModel.ToSqlExpression(
                df.SelectedField, df.SelectedDerivation, df.Parameter);

            if (!string.IsNullOrWhiteSpace(df.Alias))
                expr += $" AS [{df.Alias}]";

            parts.Add(expr);
        }

        txtPreview.Text = parts.Count > 0
            ? string.Join(", ", parts)
            : "(no derived fields)";
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        var valid = _derivedFields
            .Where(df => !string.IsNullOrWhiteSpace(df.SelectedField) &&
                         !string.IsNullOrWhiteSpace(df.SelectedDerivation))
            .ToList();

        if (valid.Count == 0)
        {
            txtStatus.Text = "Add at least one derived field.";
            return;
        }

        // Validate aliases are provided
        foreach (var df in valid)
        {
            if (string.IsNullOrWhiteSpace(df.Alias))
            {
                txtStatus.Text = "Each derived field must have an alias.";
                return;
            }
        }

        // Validate parameter is provided where required
        foreach (var df in valid)
        {
            if (DerivedFieldViewModel.ParameterRequired.Contains(df.SelectedDerivation) &&
                string.IsNullOrWhiteSpace(df.Parameter))
            {
                txtStatus.Text = $"'{df.SelectedDerivation}' requires a value for N.";
                return;
            }
        }

        Result = new DerivedFieldResult
        {
            Fields = valid.Select(df => new DerivedField
            {
                SourceField = df.SelectedField,
                Derivation = df.SelectedDerivation,
                Parameter = df.Parameter,
                Alias = df.Alias
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
