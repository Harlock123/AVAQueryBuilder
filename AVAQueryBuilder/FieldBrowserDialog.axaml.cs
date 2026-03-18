using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AVAQueryBuilder;

public partial class FieldBrowserDialog : Window
{
    public string TableName { get; set; } = string.Empty;

    public FieldBrowserDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TableName)) return;

        var query = $"SELECT TOP 100 * FROM {TableName}";
        BrowserGrid.GridTitle = query;
        Title = $"Field Browser — {TableName}";

        var connStr = AppState.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            BrowserGrid.GridTitle = "No connection string available.";
            return;
        }

        try
        {
            await BrowserGrid.PopulateFromSqlQueryAsync(connStr, query);
        }
        catch (Exception ex)
        {
            BrowserGrid.GridTitle = $"Error: {ex.Message}";
        }
    }

    private void CmdClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
