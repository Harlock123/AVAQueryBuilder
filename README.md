# AVA Query Builder

A visual SQL query builder built with .NET 9 and [Avalonia UI](https://avaloniaui.net/). Construct SELECT queries by dragging and configuring visual entities on a structured diagram canvas, then execute them against Microsoft SQL Server and view results in an integrated data grid.

## Overview

AVA Query Builder provides a graphical approach to building SQL queries. Instead of writing SQL by hand, users add visual entities to a canvas — tables, lookup joins, and limiters — and the application generates the corresponding SQL in real time. The generated query is displayed with syntax highlighting and can be executed directly against the connected database.

## Features

### Database Connection
- **Server Discovery** — Browse for SQL Server instances on the local network via UDP broadcast to the SQL Browser service (port 1434).
- **Authentication** — Supports both Windows Authentication and SQL Server Authentication.
- **Database Selection** — Queries the server for available databases and populates a dropdown for selection.
- **Connection String** — Auto-built from selections, or manually editable for environments with non-standard configurations.
- **Test Connection** — Verify connectivity before committing.

### Visual Query Building

Entities are added to an interactive canvas ([AVASdCanvas](https://github.com/harlock123/AVASdCanvas)) and connected with arrows to show relationships. Each entity type has a distinct color:

| Entity Type | Color | Description |
|---|---|---|
| **Table Source** | Light Green | A base table or view to SELECT from. Choose specific columns to include. |
| **Connected Source (Lookup)** | Light Purple | A LEFT JOIN to another table. Specify join keys and return fields. Aliased as `LOOKUP_1`, `LOOKUP_2`, etc. |
| **Limiter** | Light Red | Adds a `TOP N` clause to the query. Only one limiter at a time. |

- **Double-click** any entity to edit its configuration. Changes are reflected immediately in the generated SQL.
- **Connectors** with arrows show relationships between entities.

### SQL Generation

The query builder scans all canvas entities and generates a SQL SELECT statement:

```sql
SELECT TOP 100 dbo.Orders.OrderID, dbo.Orders.CustomerID, LOOKUP_1.CompanyName
FROM dbo.Orders
LEFT JOIN dbo.Customers AS LOOKUP_1 ON dbo.Orders.CustomerID = LOOKUP_1.CustomerID
```

The generated SQL updates live as entities are added, edited, or removed.

### Query Execution

- **Execute Query** button runs the generated SQL against the connected database.
- Results are displayed in an integrated data grid ([LAWgrid](https://github.com/harlock123/LAWgrid)) with the Results Grid tab automatically selected.

### Syntax Highlighting

The generated SQL is displayed using the [SyntaxColorizer](https://github.com/Harlock123/SyntaxColorizer) control with MS SQL language support, line numbers, and the GitHub Light theme.

### Save / Load

- **Save Query** (`.qry`) — Persists the entire query state to a JSON file: connection string, all canvas entities with positions and metadata, all connectors, and the lookup ordinal counter.
- **Load Query** — Restores a previously saved query, rehydrating the canvas, connection string, and all entity configurations.

## UI Layout

```
+----------------+----------------------------------------------+
|                |  Connection String                            |
|  [Connect]     +----------------------------------------------+
|  [Add Table]   |                                              |
|  [Add Lookup]  |  Canvas (AVASdCanvas)                        |
|  [Add Limiter] |  +--------+     +-----------+               |
|  [Add Filter]  |  | Table  |---->| Lookup    |               |
|                |  +--------+     +-----------+               |
|                |       |         +-----------+               |
|                |       +-------->| TOP 100   |               |
|  ----------    |                                              |
|  [Save Query]  +===========================[ Execute Query ]==+
|  [Load Query]  |  [Derived Query] [Results Grid]              |
|                |  SELECT TOP 100 col1, col2                   |
|                |  FROM dbo.Table                              |
+----------------+----------------------------------------------+
```

## Technology Stack

- **.NET 9** — Target framework
- **Avalonia UI 11.3** — Cross-platform UI framework
- **Microsoft.Data.SqlClient** — SQL Server connectivity
- **[LAWgrid](https://www.nuget.org/packages/LAWgrid)** — Data grid control by Lonnie Watson
- **[SyntaxColorizer](https://www.nuget.org/packages/SyntaxColorizer)** — Syntax highlighting text editor by Lonnie Watson
- **[AVASdCanvas](https://www.nuget.org/packages/AVASdCanvas)** — Structured diagram canvas by Lonnie Watson

## Building

```bash
dotnet build
```

## Running

```bash
dotnet run --project AVAQueryBuilder
```

## Project Structure

```
AVAQueryBuilder/
├── MainWindow.axaml/.cs          — Main application window and event handlers
├── App.axaml/.cs                 — Application entry point
├── Program.cs                    — Host builder
├── AppState.cs                   — Global application state (connection string)
├── QueryBuilder.cs               — SQL generation from canvas entities
├── QueryFile.cs                  — Serialization model for save/load
├── ConnectionStringDialog.axaml/.cs      — Database connection dialog
├── AddTableSourceDialog.axaml/.cs        — Table/view selection dialog
├── AddConnectedSourceDialog.axaml/.cs    — Lookup join configuration dialog
├── AddLimiterDialog.axaml/.cs            — TOP N limiter dialog
├── UnderConstructionWindow.axaml/.cs     — Placeholder dialog
├── TableSourceResult.cs          — Metadata for table entities
├── ConnectedSourceResult.cs      — Metadata for lookup entities
├── LimiterResult.cs              — Metadata for limiter entities
├── ColumnItem.cs                 — Bindable column model for checked listboxes
└── AVAQueryBuilder.csproj        — Project file
```

## License

Copyright (c) Lonnie Watson. All rights reserved.
