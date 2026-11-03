using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

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

        // Restore join type
        var joinType = existing.JoinType ?? "LEFT JOIN";
        for (var i = 0; i < cboJoinType.ItemCount; i++)
        {
            if (cboJoinType.Items[i] is ComboBoxItem item && item.Content?.ToString() == joinType)
            {
                cboJoinType.SelectedIndex = i;
                break;
            }
        }

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

            // Restore return field selections and aliases
            foreach (var col in _returnFields)
            {
                col.IsSelected = existing.ReturnFields.Contains(col.Name);
                if (existing.ColumnAliases.TryGetValue(col.Name, out var alias))
                    col.Alias = alias;
            }
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
                foreach (var t in Db.ListTables(AppState.Engine, connStr))
                    items.Add($"{(t.IsView ? "V: " : "T: ")}{t.FullName}");
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

        var columns = await Task.Run(() => Db.ListColumns(AppState.Engine, connStr, tableName));

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

    private void CmdJoinHelp_Click(object? sender, RoutedEventArgs e)
    {
        var help = new JoinTypesHelpWindow();
        help.ShowDialog(this);
    }

    private void CmdFieldBrowser_Click(object? sender, RoutedEventArgs e)
    {
        if (lstLookupTables.SelectedItem is not string selected)
        {
            txtStatus.Text = "Select a table/view to browse first.";
            return;
        }

        var tableName = ExtractTableName(selected);
        var browser = new FieldBrowserDialog { TableName = tableName };
        browser.ShowDialog(this);
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

        var aliases = _returnFields
            .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.Alias))
            .ToDictionary(c => c.Name, c => c.Alias);

        var selectedJoinType = (cboJoinType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "LEFT JOIN";

        Result = new ConnectedSourceResult
        {
            SourceTableName = SourceTable.TableName,
            JoinFieldsFromSource = joinFromSource,
            LookupTableName = ExtractTableName(selectedLookup),
            JoinFieldInLookup = joinField,
            JoinType = selectedJoinType,
            ReturnFields = returnFields,
            ColumnAliases = aliases,
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
