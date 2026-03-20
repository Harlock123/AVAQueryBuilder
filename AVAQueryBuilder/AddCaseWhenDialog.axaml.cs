using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class AddCaseWhenDialog : Window
{
    private readonly List<CaseCard> _caseCards = new();
    private List<string> _availableFields = new();

    private static readonly List<string> Operators = new()
    {
        "=", "!=", "<", ">", "<=", ">=", "LIKE", "IS NULL", "IS NOT NULL"
    };

    public TableSourceResult SourceTable { get; set; } = null!;
    public List<ConnectedSourceResult> Lookups { get; set; } = new();
    public CaseWhenResult? ExistingResult { get; set; }
    public CaseWhenResult? Result { get; private set; }

    public AddCaseWhenDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await BuildAvailableFieldsAsync();

        if (ExistingResult != null)
            RehydrateFromExisting();
        else
            AddCaseCard();

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
        foreach (var expr in ExistingResult!.Expressions)
        {
            var card = AddCaseCard();
            card.FieldCombo.SelectedItem = expr.Field;
            card.ElseBox.Text = expr.ElseValue;
            card.AliasBox.Text = expr.Alias;

            foreach (var wt in expr.Conditions)
                AddWhenRow(card, wt.Operator, wt.WhenValue, wt.ThenValue);
        }
    }

    private CaseCard AddCaseCard()
    {
        var card = new CaseCard();

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 4)
        };

        var mainStack = new StackPanel { Spacing = 6 };

        // Header: Field selector + remove button
        var headerGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        var fieldLabel = new TextBlock
        {
            Text = "CASE field:",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(fieldLabel, 0);

        var fieldCombo = new ComboBox
        {
            ItemsSource = _availableFields,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (_availableFields.Count > 0)
            fieldCombo.SelectedIndex = 0;
        Grid.SetColumn(fieldCombo, 1);

        var removeBtn = new Button
        {
            Content = "Remove",
            FontSize = 10,
            Padding = new Thickness(8, 2)
        };
        Grid.SetColumn(removeBtn, 2);

        headerGrid.Children.Add(fieldLabel);
        headerGrid.Children.Add(fieldCombo);
        headerGrid.Children.Add(removeBtn);
        mainStack.Children.Add(headerGrid);

        // Column headers for When/Then rows
        var colHeaders = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,*,30") };
        colHeaders.Children.Add(CreateLabel("Operator", 0, "0,0,4,0"));
        colHeaders.Children.Add(CreateLabel("When Value", 1, "4,0,4,0"));
        colHeaders.Children.Add(CreateLabel("Then Result", 2, "4,0,4,0"));
        mainStack.Children.Add(colHeaders);

        // When/Then rows container
        var whenStack = new StackPanel { Spacing = 2 };
        mainStack.Children.Add(whenStack);

        // Add When button
        var addWhenBtn = new Button
        {
            Content = "+ Add When",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        mainStack.Children.Add(addWhenBtn);

        // ELSE and Alias row
        var elseGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,*"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        var elseLabel = new TextBlock
        {
            Text = "ELSE:",
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(elseLabel, 0);
        var elseBox = new TextBox
        {
            Watermark = "default value",
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(elseBox, 1);
        var aliasLabel = new TextBlock
        {
            Text = "AS:",
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(aliasLabel, 2);
        var aliasBox = new TextBox
        {
            Watermark = "alias (required)",
            Margin = new Thickness(0)
        };
        Grid.SetColumn(aliasBox, 3);

        elseGrid.Children.Add(elseLabel);
        elseGrid.Children.Add(elseBox);
        elseGrid.Children.Add(aliasLabel);
        elseGrid.Children.Add(aliasBox);
        mainStack.Children.Add(elseGrid);

        border.Child = mainStack;

        card.Border = border;
        card.FieldCombo = fieldCombo;
        card.WhenStack = whenStack;
        card.ElseBox = elseBox;
        card.AliasBox = aliasBox;
        card.WhenRows = new List<WhenRow>();

        // Wire events
        removeBtn.Click += (_, _) =>
        {
            _caseCards.Remove(card);
            casePanels.Children.Remove(border);
            UpdatePreview();
        };

        addWhenBtn.Click += (_, _) =>
        {
            AddWhenRow(card);
            UpdatePreview();
        };

        _caseCards.Add(card);
        casePanels.Children.Add(border);

        // Add one default When row
        AddWhenRow(card);

        return card;
    }

    private void AddWhenRow(CaseCard card, string op = "=", string whenVal = "", string thenVal = "")
    {
        var row = new WhenRow();
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,*,30"),
            Margin = new Thickness(0, 1, 0, 1)
        };

        var opCombo = new ComboBox
        {
            ItemsSource = Operators,
            SelectedItem = op,
            Width = 90,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(opCombo, 0);

        var whenBox = new TextBox
        {
            Text = whenVal,
            Watermark = "value",
            Margin = new Thickness(4, 0, 4, 0)
        };
        Grid.SetColumn(whenBox, 1);

        var thenBox = new TextBox
        {
            Text = thenVal,
            Watermark = "result",
            Margin = new Thickness(4, 0, 4, 0)
        };
        Grid.SetColumn(thenBox, 2);

        var removeBtn = new Button
        {
            Content = "X",
            Width = 26,
            Height = 26,
            FontSize = 10,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(removeBtn, 3);

        grid.Children.Add(opCombo);
        grid.Children.Add(whenBox);
        grid.Children.Add(thenBox);
        grid.Children.Add(removeBtn);

        row.Grid = grid;
        row.OperatorCombo = opCombo;
        row.WhenBox = whenBox;
        row.ThenBox = thenBox;

        removeBtn.Click += (_, _) =>
        {
            card.WhenRows.Remove(row);
            card.WhenStack.Children.Remove(grid);
            UpdatePreview();
        };

        card.WhenRows.Add(row);
        card.WhenStack.Children.Add(grid);
    }

    private static TextBlock CreateLabel(string text, int column, string margin)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = Brushes.Gray
        };
        var parts = margin.Split(',');
        tb.Margin = new Thickness(
            double.Parse(parts[0]), double.Parse(parts[1]),
            double.Parse(parts[2]), double.Parse(parts[3]));
        Grid.SetColumn(tb, column);
        return tb;
    }

    private void CmdAddCase_Click(object? sender, RoutedEventArgs e)
    {
        AddCaseCard();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var parts = new List<string>();

        foreach (var card in _caseCards)
        {
            var expr = BuildCaseSql(card);
            if (expr != null)
                parts.Add(expr);
        }

        txtPreview.Text = parts.Count > 0
            ? string.Join(",\n", parts)
            : "(no case expressions)";
    }

    private static string? BuildCaseSql(CaseCard card)
    {
        var field = card.FieldCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(field)) return null;

        var validWhens = card.WhenRows
            .Where(r => !string.IsNullOrWhiteSpace(r.ThenBox.Text))
            .ToList();

        if (validWhens.Count == 0) return null;

        var sb = new StringBuilder("CASE");
        foreach (var r in validWhens)
        {
            var op = r.OperatorCombo.SelectedItem as string ?? "=";
            if (op is "IS NULL")
                sb.Append($" WHEN {field} IS NULL THEN '{r.ThenBox.Text}'");
            else if (op is "IS NOT NULL")
                sb.Append($" WHEN {field} IS NOT NULL THEN '{r.ThenBox.Text}'");
            else
                sb.Append($" WHEN {field} {op} '{r.WhenBox.Text}' THEN '{r.ThenBox.Text}'");
        }

        if (!string.IsNullOrWhiteSpace(card.ElseBox.Text))
            sb.Append($" ELSE '{card.ElseBox.Text}'");

        sb.Append(" END");

        if (!string.IsNullOrWhiteSpace(card.AliasBox.Text))
            sb.Append($" AS [{card.AliasBox.Text}]");

        return sb.ToString();
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        if (_caseCards.Count == 0)
        {
            txtStatus.Text = "Add at least one CASE expression.";
            return;
        }

        var expressions = new List<CaseExpression>();

        foreach (var card in _caseCards)
        {
            var field = card.FieldCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(field))
            {
                txtStatus.Text = "Select a field for each CASE expression.";
                return;
            }

            var alias = card.AliasBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(alias))
            {
                txtStatus.Text = "Each CASE expression must have an alias.";
                return;
            }

            var conditions = card.WhenRows
                .Where(r => !string.IsNullOrWhiteSpace(r.ThenBox.Text))
                .Select(r => new WhenThen
                {
                    Operator = r.OperatorCombo.SelectedItem as string ?? "=",
                    WhenValue = r.WhenBox.Text ?? "",
                    ThenValue = r.ThenBox.Text ?? ""
                }).ToList();

            if (conditions.Count == 0)
            {
                txtStatus.Text = "Each CASE expression needs at least one WHEN/THEN.";
                return;
            }

            expressions.Add(new CaseExpression
            {
                Field = field,
                Conditions = conditions,
                ElseValue = card.ElseBox.Text?.Trim() ?? "",
                Alias = alias
            });
        }

        Result = new CaseWhenResult { Expressions = expressions };
        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }

    // Internal data holders
    private class CaseCard
    {
        public Border Border { get; set; } = null!;
        public ComboBox FieldCombo { get; set; } = null!;
        public StackPanel WhenStack { get; set; } = null!;
        public TextBox ElseBox { get; set; } = null!;
        public TextBox AliasBox { get; set; } = null!;
        public List<WhenRow> WhenRows { get; set; } = new();
    }

    private class WhenRow
    {
        public Grid Grid { get; set; } = null!;
        public ComboBox OperatorCombo { get; set; } = null!;
        public TextBox WhenBox { get; set; } = null!;
        public TextBox ThenBox { get; set; } = null!;
    }
}
