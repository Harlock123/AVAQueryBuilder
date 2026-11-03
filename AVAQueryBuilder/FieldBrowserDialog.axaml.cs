using System;
using System.Threading.Tasks;
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

        var query = Dialect.Preview(AppState.Engine, TableName, 100);
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
            var engine = AppState.Engine;
            var rows = await Task.Run(() => Db.Run(engine, connStr, query));
            BrowserGrid.PopulateFromDataTable(rows);
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
