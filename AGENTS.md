# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers the features, the entity types and
the generated SQL; this file covers how the code is put together and the rules that keep it working.

## What this is

A visual query builder for Microsoft SQL Server, PostgreSQL and SQLite, in C# on .NET 10 and Avalonia 11.3. You
place entities (a base table, lookups, derived fields, CASE WHEN, group by, top N, distinct, filter, sorting) on
an `AVASdCanvas` diagram; the app turns them into a `SELECT`, shows it in a `SyntaxColorizer` editor, runs it,
and shows the rows in a `LAWgrid`. It runs on Windows, macOS and Linux. It has an AI Chat (F12) and an MCP server, so
Claude or a local model can build queries in the open window, or with no window (`--mcp`).

## Commands

```bash
dotnet build AVAQueryBuilder.sln
dotnet test AVAQueryBuilder.sln           # SQLite always; SQL Server and PostgreSQL when reachable
dotnet run --project AVAQueryBuilder      # needs a database to do anything useful (samples/clinic.db will do)
dotnet run --project AVAQueryBuilder -- --mcp   # the MCP server on stdio, no window (--attach: the open window;
                                                # --http [ADDRESS:]PORT: over the network, with a token; --rows)
AQB_SHOTS=/tmp/shots dotnet test --filter "Pictures|AiChatScreenshot"   # the Connect dialog per engine, the chat
AQB_LIVE_SERVER=http://localhost:11434 AQB_LIVE_MODEL=qwen3:1.7b dotnet test --filter LiveModelTests   # a real model, both APIs
```

The live tests read `AQB_TEST_SQLSERVER` and `AQB_TEST_POSTGRES` (connection strings). Without them they try
CCReimagined's sample servers on localhost (`docker compose -f dev/sample-databases/docker-compose.yml up -d` in
that repo) and skip the engine when nothing answers, as in CI. Each run makes its own database (SQL Server) or
schema (PostgreSQL) and drops it at the end.

## Layout

- `AVAQueryBuilder/AVAQueryBuilder.csproj`: the app (`net10.0`, nullable on, compiled bindings).
- `AVAQueryBuilder/MainWindow.axaml(.cs)`: the window. Its buttons and dialogs make their changes through
  `QueryEdits` and `Commit`, and it draws the history's current query (`ShowQuery`). It executes the query, copies
  it, exports results, and saves and loads `.qry` files. `MainWindow.Agents.cs` is the AI menu, the chat's place,
  Connect an Agent and Database Connections.
- `AVAQueryBuilder/QueryModel.cs`: `QueryEdits`, every change to a query on its saved form (`QueryFile`), placing
  each entity on the diagram and joining it to the base table; and `QueryHistory`, the undo history (a snapshot of
  the query per step, without the connection string). `QueryLook.cs` draws a `QueryFile` on an `SdCanvas` (the
  window and the agents' pictures).
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
- `AVAQueryBuilder/Agents/`: AI agents and the AI Chat.
  - `Mcp/`: the MCP server (ModelContextProtocol.Core, and .AspNetCore for the network door).
    - `QueryTools*.cs`: the tools, as `[McpServerTool]` methods. `Fields.cs`: the catalogue a call reads first
      (`Catalog`), field names from what an agent writes (`Fields.Resolve`), and `Guard`.
    - `QueryServer.cs`: the instructions every agent is told, the argument checks and the recording of each
      session's changes. `Recipes.cs`: the saved recipes, on the sample clinic's tables.
    - `Hosting.cs`: handlers touch the query only through `IQueryHost.RunAsync` and an `IQuerySession`; `QueryHost`
      runs each call on the UI thread, waiting while a dialog is open; `HeadlessSession` is `--mcp`'s.
    - `ConnectionStore.cs`: saved connections by name. `LiveListener`, `LiveBridge`, `NetworkListener`,
      `McpCommandLine` and `Workspace` are the same files as AVA Modeler's, AVASvgMaker's and AVAUIDesigner's, the
      names apart: fix them together.
  - `Chat/`: the chat's conversation and clients, a copy of AVA Modeler's `AVAModeler.Chat` (by way of
    AVAUIDesigner), so a fix there may be wanted here. This copy also lets the Anthropic API point at another
    server (`AgentSettings.ChatAnthropicUrl`: LM Studio, Ollama), with core tools and no pictures for small models.
  - `ChatPanel.cs`, `LiveAgents.cs` (`WindowSession` and the doors), `QueryLibrary.cs` (pictures, in the window's
    hidden `AgentStage`), `AgentMain.cs` (`--mcp`: Avalonia headless with Skia), `AgentSettings.cs`
    (`agents.json` in the settings folder).
- `samples/`: `clinic.sql` and the `clinic.db` it builds: made-up providers, patients and visits, for trying
  queries and for the agent tests and recipes.
- `tests/AVAQueryBuilder.Tests/`: xUnit v3. `QueryTests` builds queries the way the dialogs do and runs them on
  each engine (`TestDatabases` makes the shop tables); `ConnectionDialogTests` drives the Connect dialog with
  Avalonia.Headless, one app for the whole run.
- `Screenshots/`: the pictures used by the README.

## Rules that keep it working

- **An entity is its metadata.** The canvas entity's `Metadata` object decides what it is, both for
  `QueryBuilder` and for saving. Adding an entity type means:
  - a `*Result` class and an `Add*Dialog`;
  - a branch in `QueryBuilder.BuildQuery`;
  - a property on `EntityState` in `QueryFile.cs`, and its `EntityType` string in `QueryEdits` (`Metadata`,
    `SetMetadata`, `KindOf`, `Label`, where it's placed in `SetSingle` or an `Add…`) and its colours in `QueryLook`;
  - a `set_…` or `add_…` tool, so agents can make it too.
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
- **Every change to the query goes through `QueryEdits` and the history.** The window's buttons build a copy
  (`Snapshot()`), change it with `QueryEdits`, and `Commit` it as one undo step; the canvas is redrawn from the
  history. Don't change canvas entities directly, or undo and the agents lose track of them.
- **Agent tools.** Every change an agent makes goes through `QueryTools.Edit`: the catalogue is read first, off
  the UI thread; the work changes a copy, and only a call that succeeds becomes one undo step. A batch is several
  such steps folded into one with `QueryHistory.Squash`. A new tool needs a `[Description]` on it and on every
  parameter (snake_case names), and a place in `AgentTests.ToolNames`; every recipe is run call by call by
  `AgentTests`. Paths stay inside the working folder (`Workspace`).
- **Agents never see rows unless the person allows it.** `run_query` gives counts and columns, and the rows only
  with `AgentsSeeRows` (AI › Let AI Agents See Query Results, or `--mcp --rows`); `read_file` won't open what
  `export_results` wrote while rows were hidden. Queries run through `Db.RunReadOnly`, in a transaction that is
  always rolled back. Text that goes into SQL as written (names, a derivation's parameter) passes `Guard`; values
  are quoted with `QueryBuilder.Literal`.
- **No real model, keys, server or user settings in tests.** `SettingsIsolation` points
  `AgentSettings.FolderOverride` at a temporary folder before any test; chat tests answer from `FakeModelServer`
  and `FakeMessagesServer`; test windows get key stores and pipes of their own.
- **The custom controls come from NuGet.** `AVASdCanvas`, `LAWgrid` and `SyntaxColorizer` are the author's own
  packages, pinned in the `.csproj`. `SyntaxColorizer` 1.0.2 is built against Avalonia 11, so check all three before
  moving Avalonia past 11.3.x.

## Making a change

- Run `dotnet test` with the sample servers up, so SQL Server and PostgreSQL run too, not just SQLite.
- Build each kind of query you touched against a scratch database and run it from the app.
- Save a query, reload it, and check the canvas and SQL come back the same.
- Update `README.md` (entity table, project structure, screenshots) to match.
