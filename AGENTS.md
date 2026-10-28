# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers the features, the entity types and
the generated SQL; this file covers how the code is put together and the rules that keep it working.

## What this is

A visual query builder for Microsoft SQL Server, in C# on .NET 9 and Avalonia 11.3. You place entities (a base
table, lookups, derived fields, CASE WHEN, group by, top N, distinct, filter, sorting) on an `AVASdCanvas`
diagram; the app turns them into a `SELECT`, shows it in a `SyntaxColorizer` editor, runs it, and shows the rows
in a `LAWgrid`. It runs on Windows, macOS and Linux.

## Commands

```bash
dotnet build AVAQueryBuilder.sln
dotnet run --project AVAQueryBuilder      # needs a reachable SQL Server to do anything useful
```

There are no tests yet.

## Layout

- `AVAQueryBuilder/AVAQueryBuilder.csproj`: the only project (`net9.0`, nullable on, compiled bindings).
- `AVAQueryBuilder/MainWindow.axaml(.cs)`: the window. It adds, edits and deletes canvas entities, toggles the
  limiter and distinct entities, executes the query, copies it, exports results, and saves and loads `.qry`
  files.
- `AVAQueryBuilder/QueryBuilder.cs`: `BuildQuery` reads every canvas entity's `Metadata` and writes the SQL,
  wrapping lines at about 80 characters. This is the only place SQL text is assembled.
- `AVAQueryBuilder/*Result.cs`: the metadata object behind each entity type (`TableSourceResult`,
  `ConnectedSourceResult`, `DerivedFieldResult`, `CaseWhenResult`, `GroupByResult`, `LimiterResult`,
  `DistinctResult`, `FilterResult`, `SortingResult`).
- `AVAQueryBuilder/Add*Dialog.axaml(.cs)`: one dialog per entity type; each reads table and column lists from
  the database and returns its `*Result`.
- `AVAQueryBuilder/*ViewModel.cs`, `ColumnItem.cs`: row models for the dialogs' lists.
- `AVAQueryBuilder/ConnectionStringDialog.axaml(.cs)`: finds servers through the SQL Browser service (UDP 1434),
  lists databases, and builds the connection string (Windows or SQL authentication).
- `AVAQueryBuilder/QueryFile.cs`: the JSON shape of a `.qry` file. `AppState.cs` holds the current connection
  string.
- `AVAQueryBuilder/FieldBrowserDialog`, `JoinTypesHelpWindow`, `UnderConstructionWindow`: the data preview, the
  join help and a placeholder.
- `Screenshots/`: the pictures used by the README.

## Rules that keep it working

- **An entity is its metadata.** The canvas entity's `Metadata` object decides what it is, both for
  `QueryBuilder` and for saving. Adding an entity type means:
  - a `*Result` class and an `Add*Dialog`;
  - a branch in `QueryBuilder.BuildQuery`;
  - a property on `EntityState` in `QueryFile.cs`, plus an `EntityType` string in both the save and the load
    `switch` in `MainWindow.axaml.cs`.
- **Keep old `.qry` files loading.** They are plain `System.Text.Json` output; add new properties as optional
  and keep the existing `EntityType` strings.
- **Saved queries include the connection string**, and with SQL authentication that includes the password. Keep
  `.qry` files out of the repository.
- **T-SQL only.** The SQL uses SQL Server syntax (`TOP`, `[alias]`, `DATEPART`, `ISNULL`); the connection uses
  `Microsoft.Data.SqlClient`.
- **The custom controls come from NuGet.** `AVASdCanvas`, `LAWgrid` and `SyntaxColorizer` are the author's own
  packages, pinned in the `.csproj`. `SyntaxColorizer` 1.0.2 is built against Avalonia 11, so check all three before
  moving Avalonia past 11.3.x.

## Making a change

- Build each kind of query you touched against a scratch database and run it from the app.
- Save a query, reload it, and check the canvas and SQL come back the same.
- Update `README.md` (entity table, project structure, screenshots) to match.
