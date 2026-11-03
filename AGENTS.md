# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers the features, the entity types and
the generated SQL; this file covers how the code is put together and the rules that keep it working.

## What this is

A visual query builder for Microsoft SQL Server, PostgreSQL and SQLite, in C# on .NET 9 and Avalonia 11.3. You
place entities (a base table, lookups, derived fields, CASE WHEN, group by, top N, distinct, filter, sorting) on
an `AVASdCanvas` diagram; the app turns them into a `SELECT`, shows it in a `SyntaxColorizer` editor, runs it,
and shows the rows in a `LAWgrid`. It runs on Windows, macOS and Linux.

## Commands

```bash
dotnet build AVAQueryBuilder.sln
dotnet test AVAQueryBuilder.sln           # SQLite always; SQL Server and PostgreSQL when reachable
dotnet run --project AVAQueryBuilder      # needs a database to do anything useful (a SQLite file will do)
AQB_SHOTS=/tmp/shots dotnet test --filter Pictures   # render the Connect dialog per engine, headless
```

The live tests read `AQB_TEST_SQLSERVER` and `AQB_TEST_POSTGRES` (connection strings). Without them they try
CCReimagined's sample servers on localhost (`docker compose -f dev/sample-databases/docker-compose.yml up -d` in
that repo) and skip the engine when nothing answers, as in CI. Each run makes its own database (SQL Server) or
schema (PostgreSQL) and drops it at the end.

## Layout

- `AVAQueryBuilder/AVAQueryBuilder.csproj`: the app (`net9.0`, nullable on, compiled bindings).
- `AVAQueryBuilder/MainWindow.axaml(.cs)`: the window. It adds, edits and deletes canvas entities, toggles the
  limiter and distinct entities, executes the query, copies it, exports results, and saves and loads `.qry`
  files.
- `AVAQueryBuilder/QueryBuilder.cs`: `BuildQuery` reads every canvas entity's `Metadata` and writes the SQL for
  an engine, wrapping lines at about 80 characters. This is the only place a query is assembled.
- `AVAQueryBuilder/Dialect.cs`: everything that differs between the engines' SQL: `TOP`/`LIMIT`, alias quoting,
  filter casts, the field-browser preview and each derivation's functions.
- `AVAQueryBuilder/Database.cs`: `DbEngine` and `Db`, the only code that opens connections: table, column and
  database lists, connection tests, and `Run`, which returns a query's rows as text for the results grid.
- `AVAQueryBuilder/*Result.cs`: the metadata object behind each entity type (`TableSourceResult`,
  `ConnectedSourceResult`, `DerivedFieldResult`, `CaseWhenResult`, `GroupByResult`, `LimiterResult`,
  `DistinctResult`, `FilterResult`, `SortingResult`).
- `AVAQueryBuilder/Add*Dialog.axaml(.cs)`: one dialog per entity type; each reads table and column lists from
  the database and returns its `*Result`.
- `AVAQueryBuilder/*ViewModel.cs`, `ColumnItem.cs`: row models for the dialogs' lists.
- `AVAQueryBuilder/ConnectionStringDialog.axaml(.cs)`: picks the engine and shows its fields. For SQL Server it
  finds servers through the SQL Browser service (UDP 1434) and offers Windows or SQL authentication; PostgreSQL
  takes host[:port], user and password; SQLite takes a file. It lists databases and builds the connection string.
- `AVAQueryBuilder/QueryFile.cs`: the JSON shape of a `.qry` file. `AppState.cs` holds the current engine and
  connection string.
- `AVAQueryBuilder/FieldBrowserDialog`, `JoinTypesHelpWindow`, `UnderConstructionWindow`: the data preview, the
  join help and a placeholder.
- `tests/AVAQueryBuilder.Tests/`: xUnit v3. `QueryTests` builds queries the way the dialogs do and runs them on
  each engine (`TestDatabases` makes the shop tables); `ConnectionDialogTests` drives the Connect dialog with
  Avalonia.Headless, one app for the whole run.
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
- **Every engine's SQL comes from `Dialect`.** Don't write `TOP`, `[alias]`, `DATEPART` or other
  engine-specific SQL in `QueryBuilder` or a dialog preview; add a `Dialect` method instead. A new derivation
  needs a line in all three `Derive*` methods and a row in `QueryTests.Derivations`, which runs it on each engine.
  `Dialect.Derivations` leaves out what an engine can't do (SQLite has no `REVERSE`).
- **Connections go through `Db`.** Dialogs call `Db` with `AppState.Engine`, inside `Task.Run`; nothing else
  creates a provider's connection. Results are read into a string `DataTable` and handed to LAWgrid's
  `PopulateFromDataTable`, so all three engines show results the same way and errors reach the status bar.
- **A `.qry` file names its engine.** `QueryFile.Engine` is written as a string; files from before engines
  existed have none and load as SQL Server.
- **The custom controls come from NuGet.** `AVASdCanvas`, `LAWgrid` and `SyntaxColorizer` are the author's own
  packages, pinned in the `.csproj`. `SyntaxColorizer` 1.0.2 is built against Avalonia 11, so check all three before
  moving Avalonia past 11.3.x.

## Making a change

- Run `dotnet test` with the sample servers up, so SQL Server and PostgreSQL run too, not just SQLite.
- Build each kind of query you touched against a scratch database and run it from the app.
- Save a query, reload it, and check the canvas and SQL come back the same.
- Update `README.md` (entity table, project structure, screenshots) to match.
