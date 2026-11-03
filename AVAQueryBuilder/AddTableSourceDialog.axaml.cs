using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AVAQueryBuilder;

public partial class AddTableSourceDialog : Window
{
    private readonly ObservableCollection<string> _tables = new();
    private readonly ObservableCollection<string> _views = new();
    private readonly ObservableCollection<ColumnItem> _columns = new();
    private string? _selectedEntity;
    private bool _selectedIsView;
    private bool _suppressSelectionChanged;

    public TableSourceResult? Result { get; private set; }
    public TableSourceResult? ExistingResult { get; set; }

    public AddTableSourceDialog()
    {
        InitializeComponent();
        lstTables.ItemsSource = _tables;
        lstViews.ItemsSource = _views;
        lstColumns.ItemsSource = _columns;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await LoadTablesAndViewsAsync();
        if (ExistingResult != null)
            await RehydrateFromExisting();
    }

    private async Task RehydrateFromExisting()
    {
        var existing = ExistingResult!;
        _selectedEntity = existing.TableName;
        _selectedIsView = existing.IsView;

        _suppressSelectionChanged = true;
        if (existing.IsView)
        {
            lstTables.SelectedItem = null;
            if (_views.Contains(existing.TableName))
                lstViews.SelectedItem = existing.TableName;
        }
        else
        {
            lstViews.SelectedItem = null;
            if (_tables.Contains(existing.TableName))
                lstTables.SelectedItem = existing.TableName;
        }
        _suppressSelectionChanged = false;

        // Load columns then restore check state and aliases
        await LoadColumnsAsync(existing.TableName);
        foreach (var col in _columns)
        {
            col.IsSelected = existing.SelectedColumns.Contains(col.Name);
            if (existing.ColumnAliases.TryGetValue(col.Name, out var alias))
                col.Alias = alias;
        }
    }

    private async Task LoadTablesAndViewsAsync()
    {
        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            txtStatus.Text = "No connection string set. Connect to a database first.";
            return;
        }

        txtStatus.Text = "Loading tables and views...";

        try
        {
            var tables = new List<string>();
            var views = new List<string>();

            await Task.Run(() =>
            {
                foreach (var t in Db.ListTables(AppState.Engine, connStr))
                    (t.IsView ? views : tables).Add(t.FullName);
            });

            _tables.Clear();
            foreach (var t in tables)
                _tables.Add(t);

            _views.Clear();
            foreach (var v in views)
                _views.Add(v);

            txtStatus.Text = $"Found {tables.Count} table(s) and {views.Count} view(s).";
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private async Task LoadColumnsAsync(string schemaAndTable)
    {
        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        _columns.Clear();
        txtColumnsHeader.Text = $"Columns: {schemaAndTable}";

        try
        {
            var columns = await Task.Run(() => Db.ListColumns(AppState.Engine, connStr, schemaAndTable));

            foreach (var col in columns)
                _columns.Add(new ColumnItem { Name = col, IsSelected = true });

            txtStatus.Text = $"{columns.Count} column(s) in {schemaAndTable}.";
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error loading columns: {ex.Message}";
        }
    }

    private async void LstTables_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (lstTables.SelectedItem is not string selected) return;
        _suppressSelectionChanged = true;
        lstViews.SelectedItem = null;
        _suppressSelectionChanged = false;
        _selectedEntity = selected;
        _selectedIsView = false;
        await LoadColumnsAsync(selected);
    }

    private async void LstViews_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (lstViews.SelectedItem is not string selected) return;
        _suppressSelectionChanged = true;
        lstTables.SelectedItem = null;
        _suppressSelectionChanged = false;
        _selectedEntity = selected;
        _selectedIsView = true;
        await LoadColumnsAsync(selected);
    }

    private void CmdFieldBrowser_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedEntity))
        {
            txtStatus.Text = "Select a table or view first.";
            return;
        }

        var browser = new FieldBrowserDialog { TableName = _selectedEntity };
        browser.ShowDialog(this);
    }

    private void CmdSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var col in _columns)
            col.IsSelected = true;
    }

    private void CmdSelectNone_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var col in _columns)
            col.IsSelected = false;
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedEntity))
        {
            txtStatus.Text = "Select a table or view first.";
            return;
        }

        var selectedColumns = _columns.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        if (selectedColumns.Count == 0)
        {
            txtStatus.Text = "Select at least one column.";
            return;
        }

        var aliases = _columns
            .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.Alias))
            .ToDictionary(c => c.Name, c => c.Alias);

        Result = new TableSourceResult
        {
            TableName = _selectedEntity,
            IsView = _selectedIsView,
            SelectedColumns = selectedColumns,
            ColumnAliases = aliases
        };

        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}
