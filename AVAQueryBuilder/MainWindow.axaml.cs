using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private int _lookupOrdinal;

    private static readonly SolidColorBrush LookupFill = new(Color.Parse("#D8B4FE"));
    private static readonly SolidColorBrush LookupStroke = new(Color.Parse("#7C3AED"));
    private static readonly SolidColorBrush TableFill = new(Color.Parse("#90EE90"));
    private static readonly SolidColorBrush LimiterFill = new(Color.Parse("#FCA5A5"));
    private static readonly SolidColorBrush LimiterStroke = new(Color.Parse("#DC2626"));
    private static readonly SolidColorBrush FilterFill = new(Color.Parse("#93C5FD"));
    private static readonly SolidColorBrush FilterStroke = new(Color.Parse("#2563EB"));
    private static readonly SolidColorBrush SortingFill = new(Color.Parse("#FEF9C3"));
    private static readonly SolidColorBrush SortingStroke = new(Color.Parse("#CA8A04"));
    private static readonly SolidColorBrush DerivedFill = new(Color.Parse("#A8E0CC"));
    private static readonly SolidColorBrush DerivedStroke = new(Color.Parse("#2D8B70"));

    public MainWindow()
    {
        InitializeComponent();
        TheQuery.SyntaxTheme = BuiltInThemes.GitHubLight;
        QCanvas.ElementDoubleClick += QCanvas_ElementDoubleClick;
        QCanvas.ElementRightClick += QCanvas_ElementRightClick;
    }

    private void QCanvas_ElementRightClick(object? sender, CanvasElementEventArgs e)
    {
        if (e.Entity == null) return;

        var entity = e.Entity;
        var contextMenu = new ContextMenu();

        var editItem = new MenuItem { Header = "Edit" };
        editItem.Click += (_, _) => EditEntity(entity);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteEntity(entity);

        contextMenu.Items.Add(editItem);
        contextMenu.Items.Add(deleteItem);

        contextMenu.Open(QCanvas);
    }

    private async void EditEntity(GraphicEntity entity)
    {
        if (entity.Metadata is TableSourceResult existingTable)
        {
            var dialog = new AddTableSourceDialog { ExistingResult = existingTable };
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                var r = dialog.Result;
                entity.Metadata = null;
                entity.Label = r.TableName;
                entity.Metadata = r;
                RebuildQuery();
            }
        }
        else if (entity.Metadata is ConnectedSourceResult existingLookup)
        {
            var sourceEntity = FindSourceTableEntity(existingLookup.SourceTableName);
            if (sourceEntity?.Metadata is not TableSourceResult sourceTable) return;

            var dialog = new AddConnectedSourceDialog
            {
                SourceTable = sourceTable,
                OrdinalValue = existingLookup.OrdinalValue,
                ExistingResult = existingLookup
            };
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                var r = dialog.Result;
                entity.Metadata = null;
                entity.Label = r.LookupTableName;
                entity.Metadata = r;
                RebuildQuery();
            }
        }
        else if (entity.Metadata is LimiterResult existingLimiter)
        {
            var dialog = new AddLimiterDialog { ExistingTopCount = existingLimiter.TopCount };
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                entity.Metadata = null;
                entity.Label = $"TOP {dialog.Result.TopCount}";
                entity.Metadata = dialog.Result;
                RebuildQuery();
            }
        }
        else if (entity.Metadata is FilterResult existingFilter)
        {
            var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
            if (tableEntity?.Metadata is not TableSourceResult sourceTable) return;

            var lookups = QCanvas.Entities
                .Where(ent => ent.Metadata is ConnectedSourceResult)
                .Select(ent => (ConnectedSourceResult)ent.Metadata!)
                .OrderBy(l => l.OrdinalValue)
                .ToList();

            var dialog = new AddFilterDialog
            {
                SourceTable = sourceTable,
                Lookups = lookups,
                ExistingResult = existingFilter
            };
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                entity.Metadata = null;
                entity.Label = $"WHERE ({dialog.Result.Conditions.Count})";
                entity.Metadata = dialog.Result;
                RebuildQuery();
            }
        }
        else if (entity.Metadata is SortingResult existingSorting)
        {
            var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
            if (tableEntity?.Metadata is not TableSourceResult sourceTable) return;

            var lookups = QCanvas.Entities
                .Where(ent => ent.Metadata is ConnectedSourceResult)
                .Select(ent => (ConnectedSourceResult)ent.Metadata!)
                .OrderBy(l => l.OrdinalValue)
                .ToList();

            var dialog = new AddSortingDialog
            {
                SourceTable = sourceTable,
                Lookups = lookups,
                ExistingResult = existingSorting
            };
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                entity.Metadata = null;
                entity.Label = $"ORDER BY ({dialog.Result.Fields.Count})";
                entity.Metadata = dialog.Result;
                RebuildQuery();
            }
        }
        else if (entity.Metadata is DerivedFieldResult existingDerived)
        {
            var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
            if (tableEntity?.Metadata is not TableSourceResult sourceTable) return;

            var lookups = QCanvas.Entities
                .Where(ent => ent.Metadata is ConnectedSourceResult)
                .Select(ent => (ConnectedSourceResult)ent.Metadata!)
                .OrderBy(l => l.OrdinalValue)
                .ToList();

            var dialog = new AddDerivedDialog
            {
                SourceTable = sourceTable,
                Lookups = lookups,
                ExistingResult = existingDerived
            };
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.Result != null)
            {
                entity.Metadata = null;
                entity.Label = $"DERIVED ({dialog.Result.Fields.Count})";
                entity.Metadata = dialog.Result;
                RebuildQuery();
            }
        }
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
            _ => "Entity"
        };

        var isTable = entity.Metadata is TableSourceResult;
        var message = isTable
            ? $"Delete the {entityType}? This will remove ALL entities from the canvas."
            : $"Delete the {entityType} \"{entity.Label}\"?";

        var dialog = new Window
        {
            Title = "Confirm Delete",
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

        if (!result) return;

        if (isTable)
        {
            // Remove all entities and connectors
            QCanvas.Clear();
            _lookupOrdinal = 0;
            TheQuery.Text = string.Empty;
            UpdateConnectedSourceButtonState();
        }
        else
        {
            // Remove connectors attached to this entity
            var connectors = QCanvas.Connectors
                .Where(c => c.SourceEntityId == entity.Id || c.TargetEntityId == entity.Id)
                .ToList();
            foreach (var c in connectors)
                QCanvas.RemoveConnector(c);

            QCanvas.RemoveEntity(entity);
            RebuildQuery();
            UpdateConnectedSourceButtonState();
        }
    }

    private void QCanvas_ElementDoubleClick(object? sender, CanvasElementEventArgs e)
    {
        if (e.Entity != null)
            EditEntity(e.Entity);
    }

    private GraphicEntity? FindSourceTableEntity(string tableName)
    {
        return QCanvas.Entities.FirstOrDefault(e =>
            e.Metadata is TableSourceResult t && t.TableName == tableName);
    }

    private void RebuildQuery()
    {
        TheQuery.Text = QueryBuilder.BuildQuery(QCanvas.Entities);
    }

    private void UpdateConnectedSourceButtonState()
    {
        var hasTable = QCanvas.Entities.Any(e => e.Metadata is TableSourceResult);
        cmdAddConnectedSource.IsEnabled = hasTable;
        cmdAddDerived.IsEnabled = hasTable;
        cmdAddLimiter.IsEnabled = hasTable;
        cmdAddFilter.IsEnabled = hasTable;
        cmdAddSorting.IsEnabled = hasTable;
    }

    private void ShowUnderConstruction(string functionName)
    {
        var dialog = new UnderConstructionWindow(functionName);
        dialog.ShowDialog(this);
    }

    private async void CmdConnectToData_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionStringDialog();
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && !string.IsNullOrWhiteSpace(dialog.ResultConnectionString))
        {
            AppState.ConnectionString = dialog.ResultConnectionString;
            ConnectionStringTextBox.Text = dialog.ResultConnectionString;
        }
    }

    private async void CmdAddTableSource_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppState.ConnectionString))
        {
            ShowUnderConstruction("Connect to a database first");
            return;
        }

        var dialog = new AddTableSourceDialog();
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Result != null)
        {
            var r = dialog.Result;
            QCanvas.AddEntity(
                label: r.TableName,
                width: 160,
                height: 40,
                fill: TableFill,
                stroke: Brushes.DarkGreen,
                metadata: r
            );
            RebuildQuery();
            UpdateConnectedSourceButtonState();
        }
    }

    private async void CmdAddDerived_Click(object? sender, RoutedEventArgs e)
    {
        var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
        if (tableEntity?.Metadata is not TableSourceResult sourceTable)
        {
            ShowUnderConstruction("Add a Table Source first");
            return;
        }

        var lookups = QCanvas.Entities
            .Where(ent => ent.Metadata is ConnectedSourceResult)
            .Select(ent => (ConnectedSourceResult)ent.Metadata!)
            .OrderBy(l => l.OrdinalValue)
            .ToList();

        var dialog = new AddDerivedDialog
        {
            SourceTable = sourceTable,
            Lookups = lookups
        };

        // Pre-populate if derived already exists
        var existingDerivedEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is DerivedFieldResult);
        if (existingDerivedEntity?.Metadata is DerivedFieldResult existing)
            dialog.ExistingResult = existing;

        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Result != null)
        {
            // Remove existing derived and its connectors
            var oldDerived = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is DerivedFieldResult);
            if (oldDerived != null)
            {
                var oldConnectors = QCanvas.Connectors
                    .Where(c => c.SourceEntityId == oldDerived.Id || c.TargetEntityId == oldDerived.Id)
                    .ToList();
                foreach (var c in oldConnectors)
                    QCanvas.RemoveConnector(c);
                QCanvas.RemoveEntity(oldDerived);
            }

            // Position: next slot after lookups, limiter, filter, sorting
            var rightSideCount = QCanvas.Entities
                .Count(ent => ent.Metadata is ConnectedSourceResult or LimiterResult or FilterResult or SortingResult);
            var xPos = tableEntity.X + tableEntity.Width + 60;
            var yPos = tableEntity.Y + (rightSideCount * 55);

            var derivedEntity = QCanvas.AddEntity(
                label: $"DERIVED ({dialog.Result.Fields.Count})",
                x: xPos,
                y: yPos,
                width: 160,
                height: 40,
                fill: DerivedFill,
                stroke: DerivedStroke,
                metadata: dialog.Result
            );

            QCanvas.AddConnector(
                source: derivedEntity,
                target: tableEntity,
                sourceEndpoint: EndpointStyle.RoundDot,
                targetEndpoint: EndpointStyle.PointedArrow,
                lineBrush: Brushes.Teal,
                metadata: null
            );

            RebuildQuery();
        }
    }

    private async void CmdAddConnectedSource_Click(object? sender, RoutedEventArgs e)
    {
        var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
        if (tableEntity?.Metadata is not TableSourceResult sourceTable)
        {
            ShowUnderConstruction("Add a Table Source first");
            return;
        }

        _lookupOrdinal++;

        var dialog = new AddConnectedSourceDialog
        {
            SourceTable = sourceTable,
            OrdinalValue = _lookupOrdinal
        };
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Result != null)
        {
            var r = dialog.Result;

            var existingLookups = QCanvas.Entities.Count(ent => ent.Metadata is ConnectedSourceResult);
            var xPos = tableEntity.X + tableEntity.Width + 60;
            var yPos = tableEntity.Y + ((existingLookups - 0) * 55);

            var lookupEntity = QCanvas.AddEntity(
                label: r.LookupTableName,
                x: xPos,
                y: yPos,
                width: 160,
                height: 40,
                fill: LookupFill,
                stroke: LookupStroke,
                metadata: r
            );

            QCanvas.AddConnector(
                source: lookupEntity,
                target: tableEntity,
                sourceEndpoint: EndpointStyle.RoundDot,
                targetEndpoint: EndpointStyle.PointedArrow,
                lineBrush: Brushes.Purple,
                metadata: null
            );

            RebuildQuery();
        }
        else
        {
            _lookupOrdinal--;
        }
    }

    private async void CmdAddLimiter_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddLimiterDialog();

        // If a limiter already exists, pre-populate with its value
        var existingLimiterEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is LimiterResult);
        if (existingLimiterEntity?.Metadata is LimiterResult existing)
            dialog.ExistingTopCount = existing.TopCount;

        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Result != null)
        {
            // Remove any existing limiter entity and its connectors
            var oldLimiter = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is LimiterResult);
            if (oldLimiter != null)
            {
                var oldConnectors = QCanvas.Connectors
                    .Where(c => c.SourceEntityId == oldLimiter.Id || c.TargetEntityId == oldLimiter.Id)
                    .ToList();
                foreach (var c in oldConnectors)
                    QCanvas.RemoveConnector(c);
                QCanvas.RemoveEntity(oldLimiter);
            }

            // Position: next available slot to the right, after all lookups
            var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
            var rightSideCount = QCanvas.Entities
                .Count(ent => ent.Metadata is ConnectedSourceResult);
            var xPos = tableEntity != null ? tableEntity.X + tableEntity.Width + 60 : 20;
            var yPos = tableEntity != null ? tableEntity.Y + (rightSideCount * 55) : 20;

            var limiterEntity = QCanvas.AddEntity(
                label: $"TOP {dialog.Result.TopCount}",
                x: xPos,
                y: yPos,
                width: 160,
                height: 40,
                fill: LimiterFill,
                stroke: LimiterStroke,
                metadata: dialog.Result
            );

            // Connect table entity to limiter with arrow at the limiter
            if (tableEntity != null)
            {
                QCanvas.AddConnector(
                    source: tableEntity,
                    target: limiterEntity,
                    sourceEndpoint: EndpointStyle.RoundDot,
                    targetEndpoint: EndpointStyle.PointedArrow,
                    lineBrush: Brushes.Red,
                    metadata: null
                );
            }

            RebuildQuery();
        }
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

            await QResultsGrid.PopulateFromSqlQueryAsync(connStr, query);

            // Switch to the Results Grid tab
            tabResultsGrid.IsSelected = true;
        }
        catch (Exception ex)
        {
            ShowUnderConstruction($"Query failed: {ex.Message}");
        }
        finally
        {
            cmdExecuteQuery.IsEnabled = true;
            cmdExecuteQuery.Content = "Execute Query";
        }
    }

    private async void CmdAddFilter_Click(object? sender, RoutedEventArgs e)
    {
        var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
        if (tableEntity?.Metadata is not TableSourceResult sourceTable)
        {
            ShowUnderConstruction("Add a Table Source first");
            return;
        }

        var lookups = QCanvas.Entities
            .Where(ent => ent.Metadata is ConnectedSourceResult)
            .Select(ent => (ConnectedSourceResult)ent.Metadata!)
            .OrderBy(l => l.OrdinalValue)
            .ToList();

        var dialog = new AddFilterDialog
        {
            SourceTable = sourceTable,
            Lookups = lookups
        };

        // Pre-populate if filter already exists
        var existingFilterEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is FilterResult);
        if (existingFilterEntity?.Metadata is FilterResult existing)
            dialog.ExistingResult = existing;

        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Result != null)
        {
            // Remove existing filter and its connectors
            var oldFilter = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is FilterResult);
            if (oldFilter != null)
            {
                var oldConnectors = QCanvas.Connectors
                    .Where(c => c.SourceEntityId == oldFilter.Id || c.TargetEntityId == oldFilter.Id)
                    .ToList();
                foreach (var c in oldConnectors)
                    QCanvas.RemoveConnector(c);
                QCanvas.RemoveEntity(oldFilter);
            }

            // Position: next slot after lookups and limiter
            var rightSideCount = QCanvas.Entities
                .Count(ent => ent.Metadata is ConnectedSourceResult or LimiterResult);
            var xPos = tableEntity.X + tableEntity.Width + 60;
            var yPos = tableEntity.Y + (rightSideCount * 55);

            var filterEntity = QCanvas.AddEntity(
                label: $"WHERE ({dialog.Result.Conditions.Count})",
                x: xPos,
                y: yPos,
                width: 160,
                height: 40,
                fill: FilterFill,
                stroke: FilterStroke,
                metadata: dialog.Result
            );

            QCanvas.AddConnector(
                source: tableEntity,
                target: filterEntity,
                sourceEndpoint: EndpointStyle.RoundDot,
                targetEndpoint: EndpointStyle.PointedArrow,
                lineBrush: Brushes.Blue,
                metadata: null
            );

            RebuildQuery();
        }
    }

    private async void CmdAddSorting_Click(object? sender, RoutedEventArgs e)
    {
        var tableEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is TableSourceResult);
        if (tableEntity?.Metadata is not TableSourceResult sourceTable)
        {
            ShowUnderConstruction("Add a Table Source first");
            return;
        }

        var lookups = QCanvas.Entities
            .Where(ent => ent.Metadata is ConnectedSourceResult)
            .Select(ent => (ConnectedSourceResult)ent.Metadata!)
            .OrderBy(l => l.OrdinalValue)
            .ToList();

        var dialog = new AddSortingDialog
        {
            SourceTable = sourceTable,
            Lookups = lookups
        };

        // Pre-populate if sorting already exists
        var existingSortingEntity = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is SortingResult);
        if (existingSortingEntity?.Metadata is SortingResult existing)
            dialog.ExistingResult = existing;

        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Result != null)
        {
            // Remove existing sorting and its connectors
            var oldSorting = QCanvas.Entities.FirstOrDefault(ent => ent.Metadata is SortingResult);
            if (oldSorting != null)
            {
                var oldConnectors = QCanvas.Connectors
                    .Where(c => c.SourceEntityId == oldSorting.Id || c.TargetEntityId == oldSorting.Id)
                    .ToList();
                foreach (var c in oldConnectors)
                    QCanvas.RemoveConnector(c);
                QCanvas.RemoveEntity(oldSorting);
            }

            // Position: next slot after lookups, limiter, and filter
            var rightSideCount = QCanvas.Entities
                .Count(ent => ent.Metadata is ConnectedSourceResult or LimiterResult or FilterResult);
            var xPos = tableEntity.X + tableEntity.Width + 60;
            var yPos = tableEntity.Y + (rightSideCount * 55);

            var sortingEntity = QCanvas.AddEntity(
                label: $"ORDER BY ({dialog.Result.Fields.Count})",
                x: xPos,
                y: yPos,
                width: 160,
                height: 40,
                fill: SortingFill,
                stroke: SortingStroke,
                metadata: dialog.Result
            );

            QCanvas.AddConnector(
                source: tableEntity,
                target: sortingEntity,
                sourceEndpoint: EndpointStyle.RoundDot,
                targetEndpoint: EndpointStyle.PointedArrow,
                lineBrush: Brushes.Goldenrod,
                metadata: null
            );

            RebuildQuery();
        }
    }

    private async void CmdSaveQuery_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = StorageProvider;
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Query",
            DefaultExtension = "qry",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Query Files") { Patterns = new[] { "*.qry" } }
            }
        });

        if (file == null) return;

        try
        {
            var queryFile = new QueryFile
            {
                ConnectionString = AppState.ConnectionString,
                LookupOrdinal = _lookupOrdinal
            };

            // Serialize entities
            foreach (var entity in QCanvas.Entities)
            {
                var state = new EntityState
                {
                    Id = entity.Id,
                    Label = entity.Label,
                    X = entity.X,
                    Y = entity.Y,
                    Width = entity.Width,
                    Height = entity.Height
                };

                if (entity.Metadata is TableSourceResult tsr)
                {
                    state.EntityType = "Table";
                    state.TableSource = tsr;
                }
                else if (entity.Metadata is ConnectedSourceResult csr)
                {
                    state.EntityType = "Lookup";
                    state.ConnectedSource = csr;
                }
                else if (entity.Metadata is LimiterResult lr)
                {
                    state.EntityType = "Limiter";
                    state.Limiter = lr;
                }
                else if (entity.Metadata is FilterResult fr)
                {
                    state.EntityType = "Filter";
                    state.Filter = fr;
                }
                else if (entity.Metadata is SortingResult sr)
                {
                    state.EntityType = "Sorting";
                    state.Sorting = sr;
                }
                else if (entity.Metadata is DerivedFieldResult dfr)
                {
                    state.EntityType = "Derived";
                    state.Derived = dfr;
                }

                queryFile.Entities.Add(state);
            }

            // Serialize connectors
            foreach (var connector in QCanvas.Connectors)
            {
                queryFile.Connectors.Add(new ConnectorState
                {
                    SourceEntityId = connector.SourceEntityId,
                    TargetEntityId = connector.TargetEntityId,
                    SourceEndpoint = connector.SourceEndpoint.ToString(),
                    TargetEndpoint = connector.TargetEndpoint.ToString()
                });
            }

            var json = JsonSerializer.Serialize(queryFile, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
        }
        catch (Exception ex)
        {
            ShowUnderConstruction($"Save failed: {ex.Message}");
        }
    }

    private async void CmdLoadQuery_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = StorageProvider;
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Query",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Query Files") { Patterns = new[] { "*.qry" } }
            }
        });

        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            var queryFile = JsonSerializer.Deserialize<QueryFile>(json);
            if (queryFile == null) return;

            // Clear current state
            QCanvas.Clear();
            _lookupOrdinal = queryFile.LookupOrdinal;

            // Restore connection string
            AppState.ConnectionString = queryFile.ConnectionString;
            ConnectionStringTextBox.Text = queryFile.ConnectionString;

            // Map old entity IDs to new entities for connector restoration
            var idMap = new Dictionary<string, GraphicEntity>();

            // Restore entities
            foreach (var es in queryFile.Entities)
            {
                GraphicEntity? newEntity = null;

                switch (es.EntityType)
                {
                    case "Table":
                        newEntity = QCanvas.AddEntity(
                            label: es.Label, x: es.X, y: es.Y,
                            width: es.Width, height: es.Height,
                            fill: TableFill, stroke: Brushes.DarkGreen,
                            metadata: es.TableSource!
                        );
                        break;
                    case "Lookup":
                        newEntity = QCanvas.AddEntity(
                            label: es.Label, x: es.X, y: es.Y,
                            width: es.Width, height: es.Height,
                            fill: LookupFill, stroke: LookupStroke,
                            metadata: es.ConnectedSource!
                        );
                        break;
                    case "Limiter":
                        newEntity = QCanvas.AddEntity(
                            label: es.Label, x: es.X, y: es.Y,
                            width: es.Width, height: es.Height,
                            fill: LimiterFill, stroke: LimiterStroke,
                            metadata: es.Limiter!
                        );
                        break;
                    case "Filter":
                        newEntity = QCanvas.AddEntity(
                            label: es.Label, x: es.X, y: es.Y,
                            width: es.Width, height: es.Height,
                            fill: FilterFill, stroke: FilterStroke,
                            metadata: es.Filter!
                        );
                        break;
                    case "Sorting":
                        newEntity = QCanvas.AddEntity(
                            label: es.Label, x: es.X, y: es.Y,
                            width: es.Width, height: es.Height,
                            fill: SortingFill, stroke: SortingStroke,
                            metadata: es.Sorting!
                        );
                        break;
                    case "Derived":
                        newEntity = QCanvas.AddEntity(
                            label: es.Label, x: es.X, y: es.Y,
                            width: es.Width, height: es.Height,
                            fill: DerivedFill, stroke: DerivedStroke,
                            metadata: es.Derived!
                        );
                        break;
                }

                if (newEntity != null)
                    idMap[es.Id] = newEntity;
            }

            // Restore connectors
            foreach (var cs in queryFile.Connectors)
            {
                if (!idMap.TryGetValue(cs.SourceEntityId, out var source) ||
                    !idMap.TryGetValue(cs.TargetEntityId, out var target))
                    continue;

                var sourceEp = Enum.TryParse<EndpointStyle>(cs.SourceEndpoint, out var sep) ? sep : EndpointStyle.RoundDot;
                var targetEp = Enum.TryParse<EndpointStyle>(cs.TargetEndpoint, out var tep) ? tep : EndpointStyle.PointedArrow;

                // Determine line color from entity types
                IBrush lineBrush;
                if (source.Metadata is LimiterResult || target.Metadata is LimiterResult)
                    lineBrush = Brushes.Red;
                else if (source.Metadata is FilterResult || target.Metadata is FilterResult)
                    lineBrush = Brushes.Blue;
                else if (source.Metadata is SortingResult || target.Metadata is SortingResult)
                    lineBrush = Brushes.Goldenrod;
                else if (source.Metadata is DerivedFieldResult || target.Metadata is DerivedFieldResult)
                    lineBrush = Brushes.Teal;
                else
                    lineBrush = Brushes.Purple;

                QCanvas.AddConnector(source, target, sourceEp, targetEp, lineBrush, null);
            }

            UpdateConnectedSourceButtonState();
            RebuildQuery();
        }
        catch (Exception ex)
        {
            ShowUnderConstruction($"Load failed: {ex.Message}");
        }
    }
}
