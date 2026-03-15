using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class AddConnectedSourceDialog : Window
{
    private readonly ObservableCollection<ColumnItem> _sourceFields = new();
    private readonly ObservableCollection<string> _lookupTables = new();
    private readonly ObservableCollection<string> _joinFields = new();
    private readonly ObservableCollection<ColumnItem> _returnFields = new();

    public TableSourceResult SourceTable { get; set; } = null!;
    public int OrdinalValue { get; set; }
    public ConnectedSourceResult? ExistingResult { get; set; }
    public ConnectedSourceResult? Result { get; private set; }

    public AddConnectedSourceDialog()
    {
        InitializeComponent();
        lstSourceFields.ItemsSource = _sourceFields;
        lstLookupTables.ItemsSource = _lookupTables;
        lstJoinFields.ItemsSource = _joinFields;
        lstReturnFields.ItemsSource = _returnFields;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        // Populate source fields from the table entity
        foreach (var col in SourceTable.SelectedColumns)
            _sourceFields.Add(new ColumnItem { Name = col, IsSelected = false });

        txtSourceHeader.Text = $"Field(s) to Use in Connection\n({SourceTable.TableName})";

        // Load all tables/views for lookup selection
        await LoadTablesAndViewsAsync();

        if (ExistingResult != null)
            await RehydrateFromExisting();
    }

    private bool _isRehydrating;

    private async Task RehydrateFromExisting()
    {
        _isRehydrating = true;
        var existing = ExistingResult!;

        // Restore source field selections
        foreach (var col in _sourceFields)
            col.IsSelected = existing.JoinFieldsFromSource.Contains(col.Name);

        // Find the matching lookup table entry (has T: or V: prefix)
        var match = _lookupTables.FirstOrDefault(t => ExtractTableName(t) == existing.LookupTableName);
        if (match != null)
        {
            lstLookupTables.SelectedItem = match;

            // Wait for LstLookupTables_SelectionChanged to load columns
            await LoadLookupColumnsAsync(existing.LookupTableName);

            // Select join field
            if (_joinFields.Contains(existing.JoinFieldInLookup))
            {
                lstJoinFields.SelectedItem = existing.JoinFieldInLookup;

                // Populate return fields (same as what LstJoinFields_SelectionChanged does)
                _returnFields.Clear();
                foreach (var col in _joinFields)
                    _returnFields.Add(new ColumnItem { Name = col, IsSelected = false });
            }

            // Restore return field selections
            foreach (var col in _returnFields)
                col.IsSelected = existing.ReturnFields.Contains(col.Name);
        }

        _isRehydrating = false;
    }

    private async Task LoadTablesAndViewsAsync()
    {
        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            var items = new List<string>();
            await Task.Run(() =>
            {
                using var conn = new SqlConnection(connStr);
                conn.Open();

                using var cmd = new SqlCommand(
                    "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES " +
                    "ORDER BY TABLE_TYPE, TABLE_SCHEMA, TABLE_NAME", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
                    var type = reader.GetString(2).Trim();
                    var prefix = type == "VIEW" ? "V: " : "T: ";
                    items.Add($"{prefix}{schema}.{name}");
                }
            });

            _lookupTables.Clear();
            foreach (var item in items)
                _lookupTables.Add(item);
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error loading tables: {ex.Message}";
        }
    }

    private static string ExtractTableName(string displayItem)
    {
        // Strip the "T: " or "V: " prefix
        if (displayItem.StartsWith("V: "))
            return displayItem.Substring(3);
        if (displayItem.StartsWith("T: "))
            return displayItem.Substring(3);
        return displayItem;
    }

    private async Task LoadLookupColumnsAsync(string tableName)
    {
        _joinFields.Clear();
        _returnFields.Clear();

        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        var columns = new List<string>();
        var parts = tableName.Split('.', 2);
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

        foreach (var col in columns)
            _joinFields.Add(col);

        txtStatus.Text = $"{columns.Count} column(s) in {tableName}.";
    }

    private async void LstLookupTables_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRehydrating) return;
        if (lstLookupTables.SelectedItem is not string selected) return;

        try
        {
            await LoadLookupColumnsAsync(ExtractTableName(selected));
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private void LstJoinFields_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRehydrating) return;
        if (lstJoinFields.SelectedItem is not string _) return;

        _returnFields.Clear();
        foreach (var col in _joinFields)
            _returnFields.Add(new ColumnItem { Name = col, IsSelected = false });
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        var joinFromSource = _sourceFields.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        if (joinFromSource.Count == 0)
        {
            txtStatus.Text = "Select at least one source field to use in the connection.";
            return;
        }

        if (lstLookupTables.SelectedItem is not string selectedLookup)
        {
            txtStatus.Text = "Select a table/view to connect to.";
            return;
        }

        if (lstJoinFields.SelectedItem is not string joinField)
        {
            txtStatus.Text = "Select a field to connect with.";
            return;
        }

        var returnFields = _returnFields.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        if (returnFields.Count == 0)
        {
            txtStatus.Text = "Select at least one field to return on match.";
            return;
        }

        Result = new ConnectedSourceResult
        {
            SourceTableName = SourceTable.TableName,
            JoinFieldsFromSource = joinFromSource,
            LookupTableName = ExtractTableName(selectedLookup),
            JoinFieldInLookup = joinField,
            ReturnFields = returnFields,
            OrdinalValue = OrdinalValue
        };

        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}
