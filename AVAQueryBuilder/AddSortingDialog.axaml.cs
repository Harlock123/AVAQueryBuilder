using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class AddSortingDialog : Window
{
    private readonly ObservableCollection<SortingFieldViewModel> _sortFields = new();
    private List<string> _availableFields = new();

    public TableSourceResult SourceTable { get; set; } = null!;
    public List<ConnectedSourceResult> Lookups { get; set; } = new();
    public SortingResult? ExistingResult { get; set; }
    public SortingResult? Result { get; private set; }

    public AddSortingDialog()
    {
        InitializeComponent();
        sortingList.ItemsSource = _sortFields;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await BuildAvailableFieldsAsync();

        if (ExistingResult != null)
            RehydrateFromExisting();
        else
            AddEmptySortField();

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
        var existing = ExistingResult!;

        foreach (var sf in existing.Fields)
        {
            var vm = new SortingFieldViewModel
            {
                AvailableFields = _availableFields,
                SelectedField = sf.Field,
                SelectedDirection = sf.Direction
            };
            _sortFields.Add(vm);
        }
    }

    private void AddEmptySortField()
    {
        var vm = new SortingFieldViewModel
        {
            AvailableFields = _availableFields,
            SelectedField = _availableFields.FirstOrDefault() ?? string.Empty
        };
        _sortFields.Add(vm);
    }

    private void CmdAddSortField_Click(object? sender, RoutedEventArgs e)
    {
        AddEmptySortField();
    }

    private void CmdRemoveSortField_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SortingFieldViewModel vm)
        {
            _sortFields.Remove(vm);
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var validFields = _sortFields
            .Where(f => !string.IsNullOrWhiteSpace(f.SelectedField))
            .ToList();

        if (validFields.Count == 0)
        {
            txtPreview.Text = "(no sort fields)";
            return;
        }

        var parts = validFields.Select(f => $"{f.SelectedField} {f.SelectedDirection}");
        txtPreview.Text = "ORDER BY " + string.Join(", ", parts);
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        var validFields = _sortFields
            .Where(f => !string.IsNullOrWhiteSpace(f.SelectedField))
            .ToList();

        if (validFields.Count == 0)
        {
            txtStatus.Text = "Add at least one sort field.";
            return;
        }

        Result = new SortingResult
        {
            Fields = validFields.Select(f => new SortingField
            {
                Field = f.SelectedField,
                Direction = f.SelectedDirection
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
