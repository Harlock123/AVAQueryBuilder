using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVASdCanvas.Events;
using AVASdCanvas.Models;
using SyntaxColorizer;
using SyntaxColorizer.Themes;
using SyntaxColorizer.Linting;

namespace AVAQueryBuilder;

public partial class MainWindow : Window
{
    private bool _contextMenuOpen;

    /// <summary>The query's undo history: every step is the whole query after it (the person's edits and agents').</summary>
    private readonly QueryHistory _history = new(new QueryFile());

    /// <summary>The diagram's entities (by the canvas's own ids) and the query's entities they show (by the query's ids).</summary>
    private Dictionary<string, string> _shown = new();

    /// <summary>The .qry file the query was loaded from or saved to, or null.</summary>
    private string? _filePath;

    public MainWindow()
    {
        InitializeComponent();
        TheQuery.SyntaxTheme = BuiltInThemes.GitHubLight;
        TheQuery.Text = string.Empty;
        QCanvas.ElementDoubleClick += QCanvas_ElementDoubleClick;
        QCanvas.ElementRightClick += QCanvas_ElementRightClick;
        QCanvas.ElementHover += QCanvas_ElementHover;
        _history.Changed += UpdateHistoryState;
        KeyDown += OnWindowKeyDown;
        SetUpAgents();
        UpdateHistoryState();
    }

    private void OnWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        // The SQL box takes its own Ctrl+Z (its text); everywhere else Ctrl+Z undoes a change to the query.
        if (TheQuery.IsKeyboardFocusWithin && e.Key is Avalonia.Input.Key.Z or Avalonia.Input.Key.Y) return;
        switch (e.Key)
        {
            case Avalonia.Input.Key.F12:
                ToggleChat();
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Z when ctrl && !shift:
                Undo();
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Y when ctrl:
            case Avalonia.Input.Key.Z when ctrl && shift:
                Redo();
                e.Handled = true;
                break;
            case Avalonia.Input.Key.N when ctrl:
                MenuNew_Click(null, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Avalonia.Input.Key.O when ctrl:
                CmdLoadQuery_Click(null, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Avalonia.Input.Key.S when ctrl:
                MenuSave_Click(null, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    // ───────────────────────────── the query and its history ─────────────────────────────

    /// <summary>The query as it is now, with the entities where the person has dragged them: a copy to change and commit.</summary>
    internal QueryFile Snapshot()
    {
        var doc = _history.Current;
        foreach (var g in QCanvas.Entities)
            if (_shown.TryGetValue(g.Id, out var id) && QueryEdits.Find(doc, id) is { } e)
            {
                e.X = g.X;
                e.Y = g.Y;
                e.Width = g.Width;
                e.Height = g.Height;
            }
        return doc;
    }

    internal QueryHistory History => _history;

    internal string? CurrentFilePath => _filePath;

    /// <summary>Makes <paramref name="doc"/> the query as one undo step, and shows it.</summary>
    internal void Commit(QueryFile doc, string label)
    {
        QueryEdits.Relabel(doc);
        _history.Commit(doc, label);
        ShowQuery();
    }

    /// <summary>Draws the history's current query on the diagram and writes its SQL.</summary>
    internal void ShowQuery()
    {
        var doc = _history.Current;
        _shown = QueryLook.Fill(QCanvas, doc);
        RebuildQuery();
        UpdateConnectedSourceButtonState();
        UpdateTitle();
    }

    /// <summary>Starts again from <paramref name="doc"/> (new, loaded): nothing to undo.</summary>
    internal void Adopt(QueryFile doc, string? path)
    {
        _history.Reset(doc);
        _filePath = path;
        ShowQuery();
    }

    private void Undo()
    {
        if (!_history.CanUndo) return;
        var what = _history.UndoLabel;
        _history.Amend(Snapshot());
        _history.Undo();
        ShowQuery();
        txtStatusBar.Text = $"Undid: {what}";
    }

    private void Redo()
    {
        if (!_history.CanRedo) return;
        var what = _history.RedoLabel;
        _history.Redo();
        ShowQuery();
        txtStatusBar.Text = $"Redid: {what}";
    }

    private void MenuUndo_Click(object? sender, RoutedEventArgs e) => Undo();

    private void MenuRedo_Click(object? sender, RoutedEventArgs e) => Redo();

    private void UpdateHistoryState()
    {
        cmdUndo.IsEnabled = UndoMenuItem.IsEnabled = _history.CanUndo;
        cmdRedo.IsEnabled = RedoMenuItem.IsEnabled = _history.CanRedo;
        UndoMenuItem.Header = _history.UndoLabel is { } u ? $"_Undo {u}" : "_Undo";
        RedoMenuItem.Header = _history.RedoLabel is { } r ? $"_Redo {r}" : "_Redo";
        ToolTip.SetTip(cmdUndo, _history.UndoLabel is { } tu ? $"Undo {tu} (Ctrl+Z)" : "Undo (Ctrl+Z)");
        ToolTip.SetTip(cmdRedo, _history.RedoLabel is { } tr ? $"Redo {tr} (Ctrl+Y)" : "Redo (Ctrl+Y)");
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _filePath is { } f ? Path.GetFileName(f) : "Untitled";
        Title = $"{name}{(_history.IsDirty ? " *" : "")} - AVA Query Builder";
    }

    /// <summary>The query's entity a diagram entity shows.</summary>
    private EntityState? ModelOf(QueryFile doc, GraphicEntity g) =>
        _shown.TryGetValue(g.Id, out var id) ? QueryEdits.Find(doc, id) : null;

    private static List<ConnectedSourceResult> Lookups(QueryFile doc) =>
        QueryEdits.All<ConnectedSourceResult>(doc).OrderBy(l => l.OrdinalValue).ToList();

    private void QCanvas_ElementHover(object? sender, CanvasElementEventArgs e)
    {
        if (_contextMenuOpen) return;

        if (e.Entity == null)
        {
            ToolTip.SetIsOpen(QCanvas, false);
            ToolTip.SetTip(QCanvas, null);
            return;
        }

        var info = BuildEntitySummary(e.Entity);
        if (string.IsNullOrWhiteSpace(info))
        {
            ToolTip.SetIsOpen(QCanvas, false);
            return;
        }

        var content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFDE8")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D4C87A")),
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(8),
            Child = new TextBlock
            {
                Text = info,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            }
        };

        ToolTip.SetTip(QCanvas, content);
        ToolTip.SetIsOpen(QCanvas, true);
    }

    private static string BuildEntitySummary(GraphicEntity entity)
    {
        switch (entity.Metadata)
        {
            case TableSourceResult t:
                var type = t.IsView ? "View" : "Table";
                var cols = t.SelectedColumns.Count;
                var aliasCount = t.ColumnAliases.Count;
                var colList = string.Join(", ", t.SelectedColumns.Take(8));
                if (t.SelectedColumns.Count > 8)
                    colList += $" ... +{t.SelectedColumns.Count - 8} more";
                var summary = $"{type}: {t.TableName}\nColumns ({cols}): {colList}";
                if (aliasCount > 0)
                    summary += $"\nAliases: {aliasCount}";
                return summary;

            case ConnectedSourceResult c:
                var joinFields = string.Join(", ", c.JoinFieldsFromSource);
                var returnList = string.Join(", ", c.ReturnFields.Take(6));
                if (c.ReturnFields.Count > 6)
                    returnList += $" ... +{c.ReturnFields.Count - 6} more";
                var joinType = string.IsNullOrWhiteSpace(c.JoinType) ? "LEFT JOIN" : c.JoinType;
                var cSummary = $"Lookup: {c.LookupTableName}\n" +
                               $"Type: {joinType}\n" +
                               $"Alias: LOOKUP_{c.OrdinalValue}\n" +
                               $"Join: {joinFields} = {c.JoinFieldInLookup}\n" +
                               $"Returns ({c.ReturnFields.Count}): {returnList}";
                if (c.ColumnAliases.Count > 0)
                    cSummary += $"\nAliases: {c.ColumnAliases.Count}";
                return cSummary;

            case LimiterResult l:
                return $"TOP {l.TopCount} rows";

            case FilterResult f:
                var conditions = f.Conditions.Select(cond =>
                {
                    var field = cond.Field;
                    if (cond.CastAs is not null and not "(none)")
                        field = $"CAST({field} AS {cond.CastAs})";
                    return cond.Operator switch
                    {
                        "IS NULL" or "IS NOT NULL" => $"  {field} {cond.Operator}",
                        "IS EMPTY" => $"  {field} = ''",
                        "IS NOT EMPTY" => $"  {field} != ''",
                        "BETWEEN" => $"  {field} BETWEEN '{cond.Value}' AND '{cond.Value2}'",
                        _ => $"  {field} {cond.Operator} '{cond.Value}'"
                    };
                });
                return $"WHERE ({f.Combiner})\n{string.Join("\n", conditions)}";

            case SortingResult s:
                var sortLines = s.Fields.Select(sf => $"  {sf.Field} {sf.Direction}");
                return $"ORDER BY\n{string.Join("\n", sortLines)}";

            case DerivedFieldResult d:
                var derivedLines = d.Fields.Select(df =>
                    $"  {DerivedFieldViewModel.ToSqlExpression(df.SourceField, df.Derivation, df.Parameter)}{Dialect.As(AppState.Engine, df.Alias)}");
                return $"Derived Fields ({d.Fields.Count})\n{string.Join("\n", derivedLines)}";

            case CaseWhenResult cw:
                var caseLines = cw.Expressions.Select(expr =>
                {
                    var whenCount = expr.Conditions.Count;
                    var elseStr = string.IsNullOrWhiteSpace(expr.ElseValue) ? "" : $", ELSE '{expr.ElseValue}'";
                    return $"  {expr.Field} ({whenCount} when{(whenCount != 1 ? "s" : "")}{elseStr}){Dialect.As(AppState.Engine, expr.Alias)}";
                });
                return $"CASE/WHEN ({cw.Expressions.Count})\n{string.Join("\n", caseLines)}";

            case GroupByResult g:
                var gFields = string.Join(", ", g.GroupFields.Take(5));
                if (g.GroupFields.Count > 5)
                    gFields += $" ... +{g.GroupFields.Count - 5} more";
                var aggLines = g.Aggregates.Select(a =>
                {
                    var expr = a.Function == "COUNT(*)" ? "COUNT(*)" :
                        a.Function == "COUNT DISTINCT" ? $"COUNT(DISTINCT {a.Field})" :
                        $"{a.Function}({a.Field})";
                    return $"  {expr}{Dialect.As(AppState.Engine, a.Alias)}";
                });
                var gSummary = $"GROUP BY: {gFields}\nAggregates ({g.Aggregates.Count}):\n{string.Join("\n", aggLines)}";
                if (g.HavingConditions.Count > 0)
                {
                    var havLines = g.HavingConditions.Select(h => $"  {h.Expression} {h.Operator} {h.Value}");
                    gSummary += $"\nHAVING ({g.HavingCombiner}):\n{string.Join("\n", havLines)}";
                }
                return gSummary;

            case DistinctResult:
                return "SELECT DISTINCT — eliminates duplicate rows";

            default:
                return string.Empty;
        }
    }

    private void QCanvas_ElementRightClick(object? sender, CanvasElementEventArgs e)
    {
        ToolTip.SetIsOpen(QCanvas, false);
        ToolTip.SetTip(QCanvas, null);

        if (e.Entity == null) return;

        var entity = e.Entity;
        var contextMenu = new ContextMenu();

        var editItem = new MenuItem { Header = "Edit" };
        editItem.Click += (_, _) => EditEntity(entity);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteEntity(entity);

        contextMenu.Items.Add(editItem);
        contextMenu.Items.Add(deleteItem);

        _contextMenuOpen = true;
        contextMenu.Closed += (_, _) => _contextMenuOpen = false;

        contextMenu.Open(QCanvas);
    }

    private async void EditEntity(GraphicEntity entity)
    {
        var doc = Snapshot();
        if (ModelOf(doc, entity) is not { } model) return;
        var table = QueryEdits.BaseTable(doc)?.TableSource;
        var lookups = Lookups(doc);
        var derived = QueryEdits.All<DerivedFieldResult>(doc).FirstOrDefault();
        var caseWhen = QueryEdits.All<CaseWhenResult>(doc).FirstOrDefault();
        var groupBy = QueryEdits.All<GroupByResult>(doc).FirstOrDefault();
        object? result = null;
        string label = "";
        switch (QueryEdits.Metadata(model))
        {
            case TableSourceResult existing:
            {
                var dialog = new AddTableSourceDialog { ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit table");
                break;
            }
            case ConnectedSourceResult existing when table != null:
            {
                var dialog = new AddConnectedSourceDialog { SourceTable = table, OrdinalValue = existing.OrdinalValue, ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit lookup");
                break;
            }
            case LimiterResult existing:
            {
                var dialog = new AddLimiterDialog { ExistingTopCount = existing.TopCount };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit limiter");
                break;
            }
            case FilterResult existing when table != null:
            {
                var dialog = new AddFilterDialog { SourceTable = table, Lookups = lookups, ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit filter");
                break;
            }
            case SortingResult existing when table != null:
            {
                var dialog = new AddSortingDialog { SourceTable = table, Lookups = lookups, DerivedFields = derived, CaseWhenFields = caseWhen, GroupByFields = groupBy, ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit sorting");
                break;
            }
            case DerivedFieldResult existing when table != null:
            {
                var dialog = new AddDerivedDialog { SourceTable = table, Lookups = lookups, ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit derived fields");
                break;
            }
            case CaseWhenResult existing when table != null:
            {
                var dialog = new AddCaseWhenDialog { SourceTable = table, Lookups = lookups, ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit CASE/WHEN");
                break;
            }
            case GroupByResult existing when table != null:
            {
                var dialog = new AddGroupByDialog { SourceTable = table, Lookups = lookups, DerivedFields = derived, CaseWhenFields = caseWhen, ExistingResult = existing };
                if (await dialog.ShowDialog<bool?>(this) == true) (result, label) = (dialog.Result, "Edit group by");
                break;
            }
        }
        if (result == null) return;
        doc = Snapshot();
        if (ModelOf(doc, entity) is not { } again) return;
        QueryEdits.Replace(doc, again, result);
        Commit(doc, label);
        if (result is CaseWhenResult) await WarnGroupByIfNeeded();
    }

    private async void DeleteEntity(GraphicEntity entity)
    {
        var entityType = entity.Metadata switch
        {
            TableSourceResult => "Table Source",
            ConnectedSourceResult => "Connected Source",
            LimiterResult => "Limiter",
            FilterResult => "Filter",
            SortingResult => "Sorting",
            DerivedFieldResult => "Derived Field",
            DistinctResult => "Distinct",
            CaseWhenResult => "Case/When",
            GroupByResult => "Group By",
            _ => "Entity"
        };

        var isTable = entity.Metadata is TableSourceResult && ModelOf(_history.Current, entity) is { } m && m.Id == QueryEdits.BaseTable(_history.Current)?.Id;
        var message = isTable
            ? $"Delete the {entityType}? This will remove ALL entities from the canvas."
            : $"Delete the {entityType} \"{entity.Label}\"?";

        if (!await Ask("Confirm Delete", message)) return;

        var doc = Snapshot();
        if (ModelOf(doc, entity) is not { } model) return;
        QueryEdits.Delete(doc, model);
        Commit(doc, isTable ? "Delete the query" : $"Delete {entity.Label}");
    }

    /// <summary>A Yes/No question in a small dialog.</summary>
    private async Task<bool> Ask(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 350,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var result = false;

        var yesButton = new Button { Content = "Yes", Width = 80 };
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };

        var noButton = new Button { Content = "No", Width = 80 };
        noButton.Click += (_, _) => { dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yesButton, noButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private void QCanvas_ElementDoubleClick(object? sender, CanvasElementEventArgs e)
    {
        if (e.Entity != null)
            EditEntity(e.Entity);
    }

    private void RebuildQuery()
    {
        TheQuery.Text = QueryEdits.Sql(_history.Current, AppState.Engine);
    }

    private void UpdateConnectedSourceButtonState()
    {
        var doc = _history.Current;
        var hasTable = QueryEdits.BaseTable(doc) != null;
        cmdAddConnectedSource.IsEnabled = hasTable;
        cmdAddDerived.IsEnabled = hasTable;
        cmdAddCaseWhen.IsEnabled = hasTable;
        cmdAddGroupBy.IsEnabled = hasTable;
        cmdAddLimiter.IsEnabled = hasTable;
        cmdToggleDistinct.IsEnabled = hasTable;
        cmdAddFilter.IsEnabled = hasTable;
        cmdAddSorting.IsEnabled = hasTable;

        cmdToggleDistinct.Content = QueryEdits.First(doc, "Distinct") != null ? "Remove Distinct" : "Add Distinct";
        cmdAddLimiter.Content = QueryEdits.First(doc, "Limiter") != null ? "Remove Limiter" : "Add Limiter";
    }

    private void ShowUnderConstruction(string functionName)
    {
        var dialog = new UnderConstructionWindow(functionName);
        dialog.ShowDialog(this);
    }

    private async void CmdConnectToData_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionStringDialog(AppState.Engine);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && !string.IsNullOrWhiteSpace(dialog.ResultConnectionString))
        {
            SetConnection(dialog.ResultEngine, dialog.ResultConnectionString);
            RebuildQuery();
        }
    }

    internal void SetConnection(DbEngine engine, string connectionString, string? name = null)
    {
        AppState.Engine = engine;
        AppState.ConnectionString = connectionString;
        AppState.ConnectionName = name;
        ConnectionStringTextBox.Text = connectionString;
        txtConnectionLabel.Text = name is null
            ? $"Connection String ({Db.DisplayName(engine)})"
            : $"Connection String ({Db.DisplayName(engine)}: {name})";
    }

    /// <summary>The table or entity's dialog said OK: add its result to the query as one undo step.</summary>
    private void Add(Action<QueryFile> add, string label)
    {
        var doc = Snapshot();
        add(doc);
        Commit(doc, label);
    }

    private async void CmdAddTableSource_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppState.ConnectionString))
        {
            ShowUnderConstruction("Connect to a database first");
            return;
        }

        var dialog = new AddTableSourceDialog();
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(doc => QueryEdits.AddTable(doc, r), $"Add {r.TableName}");
    }

    /// <summary>The base table, or a notice that one is needed first.</summary>
    private TableSourceResult? RequireTable(QueryFile doc)
    {
        if (QueryEdits.BaseTable(doc)?.TableSource is { } t) return t;
        ShowUnderConstruction("Add a Table Source first");
        return null;
    }

    private async void CmdAddDerived_Click(object? sender, RoutedEventArgs e)
    {
        var doc = Snapshot();
        if (RequireTable(doc) is not { } sourceTable) return;
        var dialog = new AddDerivedDialog
        {
            SourceTable = sourceTable,
            Lookups = Lookups(doc),
            ExistingResult = QueryEdits.All<DerivedFieldResult>(doc).FirstOrDefault()
        };
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(d => QueryEdits.SetSingle(d, "Derived", r), "Set derived fields");
    }

    private async void CmdAddConnectedSource_Click(object? sender, RoutedEventArgs e)
    {
        var doc = Snapshot();
        if (RequireTable(doc) is not { } sourceTable) return;
        var dialog = new AddConnectedSourceDialog
        {
            SourceTable = sourceTable,
            OrdinalValue = doc.LookupOrdinal + 1
        };
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(d => QueryEdits.AddLookup(d, r), $"Add lookup {r.LookupTableName}");
    }

    private async void CmdAddCaseWhen_Click(object? sender, RoutedEventArgs e)
    {
        var doc = Snapshot();
        if (RequireTable(doc) is not { } sourceTable) return;
        var dialog = new AddCaseWhenDialog
        {
            SourceTable = sourceTable,
            Lookups = Lookups(doc),
            ExistingResult = QueryEdits.All<CaseWhenResult>(doc).FirstOrDefault()
        };
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
        {
            Add(d => QueryEdits.SetSingle(d, "CaseWhen", r), "Set CASE/WHEN");
            await WarnGroupByIfNeeded();
        }
    }

    private async Task WarnGroupByIfNeeded()
    {
        if (QueryEdits.First(_history.Current, "GroupBy") == null) return;

        var dialog = new Window
        {
            Title = "Group By Notice",
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var okButton = new Button { Content = "OK", Width = 80 };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "A Group By entity is present on the canvas. The CASE/WHEN expression " +
                           "you just added or edited may need to be included in the Group By fields " +
                           "for the query to be valid. Please review the Group By configuration.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { okButton }
                }
            }
        };

        await dialog.ShowDialog(this);
    }

    private async void CmdAddGroupBy_Click(object? sender, RoutedEventArgs e)
    {
        var doc = Snapshot();
        if (RequireTable(doc) is not { } sourceTable) return;
        var dialog = new AddGroupByDialog
        {
            SourceTable = sourceTable,
            Lookups = Lookups(doc),
            DerivedFields = QueryEdits.All<DerivedFieldResult>(doc).FirstOrDefault(),
            CaseWhenFields = QueryEdits.All<CaseWhenResult>(doc).FirstOrDefault(),
            ExistingResult = QueryEdits.All<GroupByResult>(doc).FirstOrDefault()
        };
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(d => QueryEdits.SetSingle(d, "GroupBy", r), "Set group by");
    }

    private async void CmdAddLimiter_Click(object? sender, RoutedEventArgs e)
    {
        // Toggle: if a limiter exists, remove it
        if (QueryEdits.First(_history.Current, "Limiter") != null)
        {
            Add(d => QueryEdits.SetSingle(d, "Limiter", null), "Remove limiter");
            return;
        }

        var dialog = new AddLimiterDialog();
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(d => QueryEdits.SetSingle(d, "Limiter", r), $"Add TOP {r.TopCount}");
    }

    private void CmdToggleDistinct_Click(object? sender, RoutedEventArgs e)
    {
        var on = QueryEdits.First(_history.Current, "Distinct") == null;
        if (on && QueryEdits.BaseTable(_history.Current) == null) return;
        Add(d => QueryEdits.SetSingle(d, "Distinct", on ? new DistinctResult() : null), on ? "Add DISTINCT" : "Remove DISTINCT");
    }

    private async void CmdCopySql_Click(object? sender, RoutedEventArgs e)
    {
        var query = TheQuery.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            txtStatusBar.Text = "No query to copy.";
            return;
        }

        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(query);
                txtStatusBar.Text = "SQL copied to clipboard.";
            }
        }
        catch (Exception ex)
        {
            txtStatusBar.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void CmdClearCanvas_Click(object? sender, RoutedEventArgs e)
    {
        Add(QueryEdits.Clear, "Clear canvas");
        QResultsGrid.PopulateFromDataTable(new System.Data.DataTable());
        txtStatusBar.Text = "Canvas cleared (Ctrl+Z brings it back).";
    }

    private async void MenuNew_Click(object? sender, RoutedEventArgs e)
    {
        if (_history.IsDirty && !await Ask("New Query", "The query has changes that haven't been saved. Start a new one anyway?")) return;
        Adopt(new QueryFile { Engine = AppState.Engine }, null);
        QResultsGrid.PopulateFromDataTable(new System.Data.DataTable());
        txtStatusBar.Text = "New query.";
    }

    private async void CmdExecuteQuery_Click(object? sender, RoutedEventArgs e)
    {
        var query = TheQuery.Text;
        var connStr = AppState.ConnectionString;

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(connStr))
        {
            ShowUnderConstruction("Set a connection string and build a query first");
            return;
        }

        try
        {
            cmdExecuteQuery.IsEnabled = false;
            cmdExecuteQuery.Content = "Executing...";
            txtStatusBar.Text = "Executing query...";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var engine = AppState.Engine;
            var rows = await Task.Run(() => Db.Run(engine, connStr, query));
            QResultsGrid.PopulateFromDataTable(rows);
            sw.Stop();

            txtStatusBar.Text = $"Returned {rows.Rows.Count:N0} row(s) in {sw.Elapsed.TotalSeconds:F1}s";

            // Switch to the Results Grid tab
            tabResultsGrid.IsSelected = true;
        }
        catch (Exception ex)
        {
            txtStatusBar.Text = $"Query failed: {ex.Message}";
            ShowUnderConstruction($"Query failed: {ex.Message}");
        }
        finally
        {
            cmdExecuteQuery.IsEnabled = true;
            cmdExecuteQuery.Content = "Execute Query";
        }
    }

    /// <summary>Rows an agent's run returned, shown in the Results Grid as Execute Query shows them.</summary>
    internal void ShowAgentRows(QueryRows rows)
    {
        QResultsGrid.PopulateFromDataTable(Db.ToTable(rows));
        tabResultsGrid.IsSelected = true;
        txtStatusBar.Text = rows.More
            ? $"An AI agent ran the query: more than {rows.Count:N0} rows (the first {rows.Rows.Count:N0} shown)."
            : $"An AI agent ran the query: {rows.Count:N0} row(s){(rows.Rows.Count < rows.Count ? $", the first {rows.Rows.Count:N0} shown" : "")}.";
    }

    private async void CmdExportToExcel_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await QResultsGrid.ExportToExcelWithDialog();
        }
        catch (Exception ex)
        {
            ShowUnderConstruction($"Export failed: {ex.Message}");
        }
    }

    private async void CmdAddFilter_Click(object? sender, RoutedEventArgs e)
    {
        var doc = Snapshot();
        if (RequireTable(doc) is not { } sourceTable) return;
        var dialog = new AddFilterDialog
        {
            SourceTable = sourceTable,
            Lookups = Lookups(doc)
        };
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(d => QueryEdits.AddFilter(d, r), "Add filter");
    }

    private async void CmdAddSorting_Click(object? sender, RoutedEventArgs e)
    {
        var doc = Snapshot();
        if (RequireTable(doc) is not { } sourceTable) return;
        var dialog = new AddSortingDialog
        {
            SourceTable = sourceTable,
            Lookups = Lookups(doc),
            DerivedFields = QueryEdits.All<DerivedFieldResult>(doc).FirstOrDefault(),
            CaseWhenFields = QueryEdits.All<CaseWhenResult>(doc).FirstOrDefault(),
            GroupByFields = QueryEdits.All<GroupByResult>(doc).FirstOrDefault(),
            ExistingResult = QueryEdits.All<SortingResult>(doc).FirstOrDefault()
        };
        if (await dialog.ShowDialog<bool?>(this) == true && dialog.Result is { } r)
            Add(d => QueryEdits.SetSingle(d, "Sorting", r), "Set sorting");
    }

    private async void MenuSave_Click(object? sender, RoutedEventArgs e)
    {
        if (_filePath is { } path)
        {
            try
            {
                WriteQuery(path);
                txtStatusBar.Text = $"Saved {Path.GetFileName(path)}.";
            }
            catch (Exception ex)
            {
                ShowUnderConstruction($"Save failed: {ex.Message}");
            }
            return;
        }
        CmdSaveQuery_Click(sender, e);
        await Task.CompletedTask;
    }

    /// <summary>Writes the query, with its connection, to a .qry file, which becomes the query's file.</summary>
    internal void WriteQuery(string path)
    {
        var doc = Snapshot();
        doc.ConnectionString = AppState.ConnectionString;
        doc.Engine = AppState.Engine;
        File.WriteAllText(path, QueryEdits.ToJson(doc));
        _history.Amend(doc);
        _history.MarkSaved();
        _filePath = path;
        UpdateTitle();
    }

    private async void CmdSaveQuery_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Query",
            DefaultExtension = "qry",
            SuggestedFileName = _filePath is { } f ? Path.GetFileName(f) : null,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Query Files") { Patterns = new[] { "*.qry" } }
            }
        });

        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            WriteQuery(path);
            txtStatusBar.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            ShowUnderConstruction($"Save failed: {ex.Message}");
        }
    }

    private async void CmdLoadQuery_Click(object? sender, RoutedEventArgs e)
    {
        if (_history.IsDirty && !await Ask("Load Query", "The query has changes that haven't been saved. Load another anyway?")) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Query",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Query Files") { Patterns = new[] { "*.qry" } }
            }
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            LoadQuery(path);
        }
        catch (Exception ex)
        {
            ShowUnderConstruction($"Load failed: {ex.Message}");
        }
    }

    /// <summary>Opens a .qry file: its connection and its query, with nothing to undo.</summary>
    internal void LoadQuery(string path)
    {
        var queryFile = QueryEdits.FromJson(File.ReadAllText(path));
        SetConnection(queryFile.Engine, queryFile.ConnectionString);
        Adopt(queryFile, path);
        QResultsGrid.PopulateFromDataTable(new System.Data.DataTable());
        txtStatusBar.Text = $"Loaded {Path.GetFileName(path)}.";
    }
}
