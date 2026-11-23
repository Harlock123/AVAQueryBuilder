using Avalonia.LogicalTree;
using Avalonia.Headless;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AVAQueryBuilder.Agents;
using AVAQueryBuilder.Agents.Chat;
using AVAQueryBuilder.Agents.Mcp;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using static AVAQueryBuilder.Tests.Agents.FakeModelServer;

namespace AVAQueryBuilder.Tests.Agents;

/// <summary>
/// Before any test runs: the AI side's settings, keys, token, connections and saved chats go to a temporary folder, so
/// no test reads or writes the person's own (and none ever finds a real API key).
/// </summary>
internal static class SettingsIsolation
{
    [ModuleInitializer]
    internal static void Isolate() =>
        AgentSettings.FolderOverride = Directory.CreateTempSubdirectory("avaquerybuilder-agents-").FullName;
}

internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// The MCP server and the AI Chat, driven as agents drive them: through an in-process MCP client over the real server,
/// on Avalonia's headless platform, against a copy of the sample clinic database (samples/clinic.db). The chat's
/// models are scripted fakes of both APIs; no real model, key or database server is ever used.
/// </summary>
public sealed class AgentTests
{
    /// <summary>Every tool the server offers. A new tool goes here, with descriptions on it and its arguments.</summary>
    internal static readonly string[] ToolNames =
    [
        "get_query", "get_sql", "new_query", "open_query", "save_query", "list_connections", "connect", "add_connection",
        "list_tables", "describe_table", "find_columns", "list_options", "build_query", "add_table", "set_columns",
        "add_lookup", "set_lookup", "add_filter", "set_filter", "set_derived", "set_case_when", "set_group_by", "set_sort",
        "set_limit", "set_distinct", "remove", "check_query", "run_query", "export_results", "render_view", "list_files",
        "read_file", "undo", "redo", "batch", "get_recording", "save_steps", "run_steps", "checkpoint", "diff",
        "render_checkpoint", "restore_checkpoint",
    ];

    /// <summary>The sample database in the repository.</summary>
    internal static string SampleDatabase
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "samples", "clinic.db"))) return Path.Combine(dir.FullName, "samples", "clinic.db");
            throw new FileNotFoundException("samples/clinic.db wasn't found above the test folder.");
        }
    }

    /// <summary>A server on a query of its own, in a temporary working folder holding a copy of the clinic database.</summary>
    internal sealed class Rig : IDisposable
    {
        private readonly Window _window;

        public HeadlessDesk Desk { get; }
        public string Root { get; } = Directory.CreateTempSubdirectory("aqb-agent-").FullName;
        public ConnectionStore Connections { get; } = new(null);
        public McpToolBridge Bridge { get; }
        public HeadlessSession Session => (HeadlessSession)Desk.Session;
        public string Database => Path.Combine(Root, "clinic.db");

        public Rig(bool seeRows = false, bool connect = true)
        {
            Desk = new HeadlessDesk(null, seeRows);
            File.Copy(SampleDatabase, Database);
            Connections.Put(new DataConnection { Name = "clinic", Provider = "sqlite", ConnectionString = $"Data Source={Database};Pooling=False" }, save: false);
            var stage = new Panel();
            _window = new Window { Width = 20, Height = 20, Content = stage };
            _window.Show();
            Bridge = Wait(McpToolBridge.StartAsync(new QueryHost(new AvaloniaThread(), Desk, new QueryLibrary(stage), Root, Connections)));
            if (connect) Ok("connect", new { name = "clinic" });
        }

        public ToolOutcome Call(string tool, object? args = null) =>
            Wait(Bridge.CallAsync(tool, JsonSerializer.SerializeToElement(args ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

        public JsonElement Ok(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.False(o.IsError, $"{tool} failed: {o.Text}");
            return o.Text.StartsWith('{') ? JsonDocument.Parse(o.Text).RootElement.Clone() : default;
        }

        public string Error(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.True(o.IsError, $"{tool} should have failed: {o.Text}");
            return o.Text;
        }

        public string Sql => QueryEdits.Sql(Session.Document, Session.Engine);

        public void Dispose()
        {
            Wait(Bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            _window.Close();
            SqliteConnection.ClearAllPools();
        }
    }

    internal static T Wait<T>(Task<T> task, int seconds = 12)
    {
        for (var i = 0; i < seconds * 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Runs on the headless UI thread with a rig.</summary>
    private static void WithRig(Action<Rig> test, bool seeRows = false, bool connect = true) => HeadlessApp.Run(() =>
    {
        using var rig = new Rig(seeRows, connect);
        test(rig);
    });

    [Fact]
    public void The_tools_are_the_ones_listed_and_each_says_what_it_and_its_arguments_are() => WithRig(rig =>
    {
        var tools = Wait(rig.Bridge.ListToolsAsync());
        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has no description");
            if (t.InputSchema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    Assert.True(p.Value.TryGetProperty("description", out _), $"{t.Name}.{p.Name} has no description");
                    Assert.Matches("^[a-z][a-z0-9_]*$", p.Name);
                }
        }
        foreach (var name in ChatTools.Core) Assert.Contains(name, ToolNames);
    }, connect: false);

    [Fact]
    public void It_reads_the_catalogue_and_suggests_joins_from_the_foreign_keys() => WithRig(rig =>
    {
        var tables = rig.Ok("list_tables");
        Assert.Equal(["main.patients", "main.providers", "main.visits"], tables.GetProperty("tables").EnumerateArray().Select(x => x.GetString()).Order());
        Assert.Equal("main.active_patients", tables.GetProperty("views")[0].GetString());

        var visits = rig.Ok("describe_table", new { table = "visits" });
        Assert.Equal("main.visits", visits.GetProperty("table").GetString());
        Assert.True(visits.GetProperty("columns")[0].GetProperty("key").GetBoolean());
        var lookups = visits.GetProperty("lookups").EnumerateArray().Select(l => l.GetProperty("table").GetString()).Order();
        Assert.Equal(["main.patients", "main.providers"], lookups);
        var patients = rig.Ok("describe_table", new { table = "patients" });
        Assert.Contains(patients.GetProperty("referenced_by").EnumerateArray(), r => r.GetString()!.StartsWith("main.visits.patient_id"));

        var found = rig.Ok("find_columns", new { text = "birth" });
        Assert.Contains(found.GetProperty("found").EnumerateArray(), f => f.GetProperty("column").GetString() == "birth_date");
        Assert.Contains("There's no table or view \"patient\"", rig.Error("describe_table", new { table = "patient" }));
    });

    [Fact]
    public void A_query_built_in_one_call_is_one_undo_step_and_the_database_checks_it() => WithRig(rig =>
    {
        var built = rig.Ok("build_query", new
        {
            query = new
            {
                table = "patients",
                columns = new[] { "first_name", "last_name", "city" },
                aliases = new Dictionary<string, string> { ["last_name"] = "Last Name" },
                lookups = new[] { new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name" }, aliases = new Dictionary<string, string> { ["name"] = "Provider" } } },
                filters = new object[] { new { field = "status", value = "active" }, new { field = "LOOKUP_1.specialty", op = "=", value = "Cardiology" } },
                sort = new[] { new { field = "last_name" } },
                limit = 5,
            },
        });
        var sql = built.GetProperty("sql").GetString()!;
        Assert.Contains("LEFT JOIN main.providers AS LOOKUP_1 ON main.patients.provider_id = LOOKUP_1.id", sql);
        Assert.Contains("main.patients.status = 'active'", sql);
        Assert.Contains("LIMIT 5", sql);
        Assert.Equal(1, rig.Session.History.Index);
        Assert.Equal(5, rig.Session.Document.Entities.Count);

        var check = rig.Ok("check_query");
        Assert.True(check.GetProperty("ok").GetBoolean());
        Assert.Equal(["first_name", "Last Name", "city", "Provider"], check.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));

        Assert.Contains("replace true", rig.Error("build_query", new { query = new { table = "visits" } }));
        rig.Ok("undo");
        Assert.Empty(rig.Session.Document.Entities);
        rig.Ok("redo");
        Assert.Equal(5, rig.Session.Document.Entities.Count);
    });

    [Fact]
    public void Running_a_query_gives_the_count_and_columns_and_the_rows_only_when_allowed()
    {
        foreach (var see in new[] { false, true })
            WithRig(rig =>
            {
                rig.Ok("add_table", new { table = "patients", columns = new[] { "first_name", "last_name" } });
                rig.Ok("add_filter", new { conditions = new[] { new { field = "status", value = "active" } } });
                var run = rig.Ok("run_query", new { max_rows = 3 });
                using var c = new SqliteConnection($"Data Source={rig.Database};Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM patients WHERE status = 'active'";
                Assert.Equal(Convert.ToInt32(cmd.ExecuteScalar()), run.GetProperty("rows").GetInt32());
                Assert.Equal(["first_name", "last_name"], run.GetProperty("columns").EnumerateArray().Select(x => x.GetString()));
                if (see) Assert.Equal(3, run.GetProperty("data").GetArrayLength());
                else
                {
                    Assert.False(run.TryGetProperty("data", out _));
                    Assert.Contains("haven't let AI agents see", run.GetProperty("note").GetString());
                }
            }, seeRows: see);
    }

    [Fact]
    public void Values_are_quoted_so_they_can_never_change_the_statement() => WithRig(rig =>
    {
        rig.Ok("add_table", new { table = "patients", columns = new[] { "last_name" } });
        rig.Ok("add_filter", new { conditions = new[] { new { field = "last_name", value = "O'Brien'; DROP TABLE patients; --" } } });
        Assert.Contains("'O''Brien''; DROP TABLE patients; --'", rig.Sql);
        Assert.Equal(0, rig.Ok("run_query").GetProperty("rows").GetInt32());
        Assert.Contains("can't contain", rig.Error("add_filter", new { conditions = new[] { new { field = "last_name; DROP TABLE patients", value = "x" } } }));
        Assert.Contains("can't contain", rig.Error("set_derived", new { fields = new[] { new { field = "birth_date", derivation = "DATEDIFF (Years)", parameter = "DATE('now')); DELETE FROM patients; --" } } }));
        Assert.Equal(62, Count(rig, "patients"));
    });

    [Fact]
    public void A_read_only_run_never_changes_the_database() => WithRig(rig =>
    {
        var cs = $"Data Source={rig.Database};Pooling=False";
        Assert.ThrowsAny<Exception>(() => Db.RunReadOnly(DbEngine.Sqlite, cs, "DELETE FROM visits", 10));
        Assert.Equal(287, Count(rig, "visits"));
    });

    private static int Count(Rig rig, string table)
    {
        using var c = new SqliteConnection($"Data Source={rig.Database};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void Fields_are_found_by_short_names_aliases_and_lookup_table_names() => WithRig(rig =>
    {
        rig.Ok("add_table", new { table = "visits", columns = new[] { "visit_date", "charge" } });
        rig.Ok("add_lookup", new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name", "specialty" }, join = "inner" });
        rig.Ok("set_derived", new { fields = new[] { new { field = "visit_date", derivation = "year", alias = "Year" } } });
        rig.Ok("set_group_by", new
        {
            fields = new[] { "providers.specialty", "Year" },
            aggregates = new object[] { new { function = "sum", field = "charge", alias = "Charges" }, new { function = "count(*)", alias = "Visits" } },
            having = new[] { new { aggregate = "Visits", op = ">", value = "5" } },
        });
        rig.Ok("set_sort", new { fields = new[] { new { field = "Charges", direction = "desc" } } });
        var sql = System.Text.RegularExpressions.Regex.Replace(rig.Sql, @"\s+", " ");
        Assert.Contains("GROUP BY LOOKUP_1.specialty, CAST(STRFTIME('%Y', main.visits.visit_date) AS INTEGER)", sql);
        Assert.Contains("HAVING COUNT(*) > 5", sql);
        Assert.Contains("ORDER BY SUM(main.visits.charge) DESC", sql);
        Assert.True(rig.Ok("check_query").GetProperty("ok").GetBoolean());
        Assert.Contains("could be", rig.Error("add_filter", new { conditions = new[] { new { field = "id", value = "1" } } }));
        Assert.Contains("isn't a field", rig.Error("add_filter", new { conditions = new[] { new { field = "nope", value = "1" } } }));
    });

    [Fact]
    public void Batches_are_one_step_or_nothing_and_checkpoints_compare_and_restore() => WithRig(rig =>
    {
        rig.Ok("checkpoint", new { name = "empty" });
        var dry = rig.Ok("batch", new
        {
            dry_run = true,
            steps = new object[]
            {
                new { tool = "add_table", args = new { table = "patients" } },
                new { tool = "set_limit", args = new { rows = 10 } },
            },
        });
        Assert.Equal(2, dry.GetProperty("would_change").GetProperty("added").GetArrayLength());
        Assert.Empty(rig.Session.Document.Entities);

        Assert.Contains("Step 2", rig.Error("batch", new
        {
            steps = new object[] { new { tool = "add_table", args = new { table = "patients" } }, new { tool = "add_lookup", args = new { table = "nowhere", on = "id", from = new[] { "id" }, columns = new[] { "id" } } } },
        }));
        Assert.Empty(rig.Session.Document.Entities);
        Assert.False(rig.Session.History.CanRedo);

        rig.Ok("batch", new { steps = new object[] { new { tool = "add_table", args = new { table = "patients" } }, new { tool = "set_distinct", args = new { on = true } } } });
        Assert.Equal(1, rig.Session.History.Index);
        var diff = rig.Ok("diff", new { since = "empty" });
        Assert.Equal(2, diff.GetProperty("added").GetArrayLength());
        Assert.StartsWith("SELECT DISTINCT", diff.GetProperty("sql_now").GetString());
        var picture = rig.Call("render_checkpoint", new { name = "empty" });
        Assert.False(picture.IsError, picture.Text);
        rig.Ok("restore_checkpoint", new { name = "empty" });
        Assert.Empty(rig.Session.Document.Entities);
        rig.Ok("undo");
        Assert.Equal(2, rig.Session.Document.Entities.Count);
    });

    [Fact]
    public void Steps_files_run_again_with_other_values() => WithRig(rig =>
    {
        rig.Ok("add_table", new { table = "visits", columns = new[] { "visit_date" } });
        rig.Ok("add_filter", new { conditions = new[] { new { field = "kind", value = "Telehealth" } } });
        var rec = rig.Ok("get_recording");
        Assert.Equal(["add_table", "add_filter"], rec.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("tool").GetString()));
        rig.Ok("save_steps", new
        {
            path = "kind",
            steps = new object[]
            {
                new { tool = "build_query", args = new { replace = true, query = new { table = "visits", columns = new[] { "visit_date" }, filters = new[] { new { field = "kind", value = "${kind}" } } } } },
            },
            parameters = new { kind = "Telehealth" },
        });
        rig.Ok("run_steps", new { path = "kind.avasteps.json", values = new { kind = "Procedure" } });
        Assert.Contains("'Procedure'", rig.Sql);
    });

    [Fact]
    public void Queries_save_open_and_export_and_exported_rows_stay_hidden_when_rows_are() => WithRig(rig =>
    {
        rig.Ok("add_table", new { table = "providers" });
        rig.Ok("save_query", new { path = "queries/providers" });
        var saved = QueryEdits.FromJson(File.ReadAllText(Path.Combine(rig.Root, "queries", "providers.qry")));
        Assert.Equal(DbEngine.Sqlite, saved.Engine);
        Assert.Contains("clinic.db", saved.ConnectionString);
        Assert.False(rig.Session.History.IsDirty);
        rig.Ok("new_query");
        rig.Ok("open_query", new { path = "queries/providers.qry" });
        Assert.Single(rig.Session.Document.Entities);

        var export = rig.Ok("export_results", new { path = "out/providers.csv" });
        Assert.Equal(6, export.GetProperty("rows").GetInt32());
        Assert.Equal(7, File.ReadAllLines(Path.Combine(rig.Root, "out", "providers.csv")).Length);
        Assert.Contains("hasn't let AI agents see", rig.Error("read_file", new { path = "out/providers.csv" }));
        rig.Ok("export_results", new { path = "out/providers.xlsx" });
        Assert.True(new FileInfo(Path.Combine(rig.Root, "out", "providers.xlsx")).Length > 1000);
        Assert.Contains("outside the working folder", rig.Error("export_results", new { path = "../x.csv" }));
    });

    [Fact]
    public void Connections_show_their_server_but_never_their_password() => WithRig(rig =>
    {
        rig.Connections.Put(new DataConnection { Name = "ehr", Provider = "postgres", ConnectionString = "Host=db1;Database=ehr;Username=reader;Password=Sekrit-99" }, save: false);
        var list = rig.Call("list_connections");
        Assert.Contains("Host=db1", list.Text);
        Assert.DoesNotContain("Sekrit", list.Text);
        Assert.Contains("PostgreSQL", list.Text);
        Assert.DoesNotContain("Sekrit", rig.Call("get_query").Text);
    });

    [Fact]
    public void The_sql_can_be_written_for_another_engine() => WithRig(rig =>
    {
        rig.Ok("add_table", new { table = "patients", columns = new[] { "last_name" } });
        rig.Ok("set_limit", new { rows = 10 });
        var sqlServer = rig.Ok("get_sql", new { engine = "SQL Server" }).GetProperty("sql").GetString()!;
        Assert.StartsWith("SELECT TOP 10", sqlServer);
        Assert.EndsWith("LIMIT 10", rig.Ok("get_sql", new { engine = "postgres" }).GetProperty("sql").GetString()!);
    });

    [Fact]
    public void The_diagram_is_drawn_for_the_agent_with_the_sql() => WithRig(rig =>
    {
        rig.Ok("build_query", new { query = new { table = "visits", lookups = new[] { new { table = "patients", on = "id", from = new[] { "patient_id" }, columns = new[] { "last_name" } } }, limit = 3 } });
        var picture = rig.Call("render_view");
        Assert.False(picture.IsError, picture.Text);
        var png = Assert.Single(picture.Images);
        Assert.True(png.Data.Length > 1000);
        Assert.Contains("LEFT JOIN", picture.Text);
    });

    [Fact]
    public void Every_recipe_runs_call_by_call() => HeadlessApp.Run(() =>
    {
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null).ToList();
        Assert.Equal(8, recipes.Count);
        foreach (var recipe in recipes)
        {
            using var rig = new Rig();
            // explain_saved_query opens a saved query; port_query moves the open one.
            rig.Ok("build_query", new { query = new { table = "active_patients", columns = new[] { "last_name" } } });
            rig.Ok("save_query", new { path = "queries/active-patients.qry" });
            var text = (string)recipe.Invoke(null, recipe.GetParameters().Select(_ => (object?)null).ToArray())!;
            var calls = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(calls);
            foreach (var call in calls)
            {
                var rest = call[Recipes.CallMarker.Length..];
                var space = rest.IndexOf(' ');
                var tool = rest[..space];
                var args = JsonDocument.Parse(rest[(space + 1)..]).RootElement;
                var outcome = Wait(rig.Bridge.CallAsync(tool, args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
                Assert.False(outcome.IsError, $"{recipe.Name}: {tool} failed: {outcome.Text}");
            }
        }
    });

    [Fact]
    public void Removing_the_base_table_takes_the_query_with_it() => WithRig(rig =>
    {
        rig.Ok("build_query", new { query = new { table = "visits", sort = new[] { new { field = "visit_date" } } } });
        var table = rig.Session.Document.Entities[0].Id;
        rig.Ok("remove", new { id = table });
        Assert.Empty(rig.Session.Document.Entities);
        rig.Ok("undo");
        var sort = rig.Session.Document.Entities.First(e => e.EntityType == "Sorting").Id;
        rig.Ok("remove", new { id = sort });
        Assert.Single(rig.Session.Document.Entities);
    });

    [Fact]
    public void Old_query_files_still_open_with_their_own_entity_ids() => WithRig(rig =>
    {
        var old = """
            {"ConnectionString":"","LookupOrdinal":0,"Entities":[{"Id":"3f2b-guid","Label":"main.patients","X":20,"Y":20,"Width":160,"Height":40,"EntityType":"Table","TableSource":{"TableName":"main.patients","IsView":false,"SelectedColumns":["last_name"],"ColumnAliases":{}}}],"Connectors":[]}
            """;
        Directory.CreateDirectory(Path.Combine(rig.Root, "old"));
        File.WriteAllText(Path.Combine(rig.Root, "old", "q.qry"), old);
        rig.Ok("open_query", new { path = "old/q.qry" });
        rig.Ok("connect", new { name = "clinic" });
        rig.Ok("set_limit", new { rows = 2 });
        Assert.Contains("3f2b-guid", rig.Session.Document.Entities.Select(e => e.Id));
        Assert.Contains("e1", rig.Session.Document.Entities.Select(e => e.Id));
        Assert.Equal(2, rig.Ok("run_query").GetProperty("rows").GetInt32());
    });

    // ───────────────────────────── the chat ─────────────────────────────

    [Fact]
    public void A_chat_on_a_local_openai_compatible_model_builds_a_query_and_its_reply_can_be_undone() => WithRig(rig =>
    {
        var server = new FakeModelServer("http://localhost:1234/v1")
            .Reply(Say("I'll count visits per month."), Call("c1", "build_query", new
            {
                query = new
                {
                    table = "visits",
                    derived = new[] { new { field = "visit_date", derivation = "Year Part", alias = "Year" }, new { field = "visit_date", derivation = "Month Part", alias = "Month" } },
                    group_by = new { fields = new[] { "Year", "Month" }, aggregates = new[] { new { function = "COUNT(*)", alias = "Visits" } } },
                },
            }))
            .Reply(Call("c2", "run_query", new { }))
            .Reply(Say("Done: visits per month."));
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen/qwen3-8b" };
        Wait(chat.SendAsync("visits per month").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        Assert.Equal(["build_query", "run_query"], chat.Entries.OfType<ToolEntry>().Select(t => t.Name));
        Assert.All(chat.Entries.OfType<ToolEntry>(), t => Assert.False(t.IsError, t.Result));
        Assert.Contains("AVA Query Builder", (string?)server.Requests[0]["messages"]![0]!["content"]);
        Assert.Contains("GROUP BY", rig.Sql);

        var undo = Assert.Single(chat.Entries.OfType<ReplyUndoEntry>());
        Assert.Contains("+3 entities", undo.Summary);
        Assert.True(Wait(chat.UndoReplyAsync(undo)));
        Assert.Empty(rig.Session.Document.Entities);
    });

    [Fact]
    public void A_chat_on_a_local_server_that_speaks_the_messages_api_builds_a_query() => WithRig(rig =>
    {
        var claude = new FakeMessagesServer()
            .Reply(new FakeMessagesServer.ToolUse("t1", "build_query", new { query = new { table = "providers", columns = new[] { "name", "specialty" } } }))
            .Reply(new FakeMessagesServer.Words("There are six providers."));
        var client = new Anthropic.AnthropicClient { ApiKey = "local", BaseUrl = "http://localhost:1234", HttpClient = new HttpClient(claude) };
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.Anthropic, Client = client, Model = "qwen/qwen3-8b", ClaudeServer = "localhost:1234", ClaudeCoreToolsOnly = true, ClaudeSeesPictures = false };
        Wait(chat.SendAsync("list the providers").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        Assert.Equal("http://localhost:1234/v1/messages", claude.Urls[0]);
        Assert.Equal("qwen/qwen3-8b", (string?)claude.Requests[0]["model"]);
        var offered = claude.Requests[0]["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(ChatTools.Core.Order(), offered.Order());
        Assert.Contains("core set", (string?)claude.Requests[0]["system"]![0]!["text"]);
        Assert.Single(rig.Session.Document.Entities);
        Assert.Equal("The model", chat.Who);
        Assert.Null(chat.Price);
    });

    // ───────────────────────────── the window ─────────────────────────────

    [Fact]
    public void Agents_build_in_the_open_window_and_the_person_undoes_their_steps() => HeadlessApp.Run(() =>
    {
        var root = Directory.CreateTempSubdirectory("aqb-window-").FullName;
        var db = Path.Combine(root, "clinic.db");
        File.Copy(SampleDatabase, db);
        var window = new MainWindow();
        window.Show();
        window.SetConnection(DbEngine.Sqlite, $"Data Source={db};Pooling=False");
        var agents = window.Agents;
        agents.Connections = new ConnectionStore(null);
        var bridge = Wait(McpToolBridge.StartAsync(agents.ChatHost(root, () => "Claude")));
        try
        {
            ToolOutcome Call(string tool, object args) =>
                Wait(bridge.CallAsync(tool, JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
            var built = Call("build_query", new { query = new { table = "patients", columns = new[] { "last_name" }, filters = new[] { new { field = "status", value = "active" } }, limit = 5 } });
            Assert.False(built.IsError, built.Text);
            Assert.Equal(3, window.QCanvas.Entities.Count);
            Assert.Contains("LIMIT 5", window.TheQuery.Text);
            Assert.True(window.History.CanUndo);

            var run = Call("run_query", new { });
            Assert.False(run.IsError, run.Text);
            Assert.True(window.tabResultsGrid.IsSelected);

            // The person's Ctrl+Z takes the agent's step back.
            window.Focus();
            var key = new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Z, KeyModifiers = Avalonia.Input.KeyModifiers.Control };
            window.RaiseEvent(key);
            Assert.Empty(window.QCanvas.Entities);
            Assert.Equal("", window.TheQuery.Text);

            // The rows stay out of reach until the person allows them.
            Assert.False(JsonDocument.Parse(Call("get_query", new { }).Text).RootElement.GetProperty("agents_see_rows").GetBoolean());
            window.SetAgentsSeeRows(true);
            Assert.True(JsonDocument.Parse(Call("get_query", new { }).Text).RootElement.GetProperty("agents_see_rows").GetBoolean());
            window.SetAgentsSeeRows(false);
        }
        finally
        {
            Wait(bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            window.History.MarkSaved();
            window.Close();
            SqliteConnection.ClearAllPools();
        }
    });

    [Fact]
    public void The_window_s_own_buttons_go_through_the_same_history() => HeadlessApp.Run(() =>
    {
        var window = new MainWindow();
        window.Show();
        var doc = window.Snapshot();
        QueryEdits.AddTable(doc, new TableSourceResult { TableName = "main.patients", SelectedColumns = ["last_name"] });
        window.Commit(doc, "Add main.patients");
        doc = window.Snapshot();
        QueryEdits.SetSingle(doc, "Distinct", new DistinctResult());
        window.Commit(doc, "Add DISTINCT");
        Assert.Equal("Remove Distinct", window.cmdToggleDistinct.Content);
        Assert.True(window.cmdUndo.IsEnabled);
        window.cmdUndo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal("Add Distinct", window.cmdToggleDistinct.Content);
        Assert.Single(window.QCanvas.Entities);
        Assert.EndsWith("*", window.Title!.Replace(" - AVA Query Builder", ""));
        window.History.MarkSaved();
        window.Close();
    });
}

/// <summary>The window's own buttons and dialogs, clicked through, on the shared history.</summary>
public sealed class WindowFlowTests
{
    private static void Settle(int times = 30)
    {
        for (var i = 0; i < times; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    private static T Owned<T>(Window owner) where T : Window
    {
        for (var i = 0; i < 400; i++)
        {
            if (owner.OwnedWindows.OfType<T>().FirstOrDefault() is { } w) return w;
            Settle(1);
        }
        throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} opened.");
    }

    private static void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void Add_table_through_its_dialog_then_delete_it_after_saying_yes_and_undo_both() => HeadlessApp.Run(() =>
    {
        var root = Directory.CreateTempSubdirectory("aqb-flow-").FullName;
        var db = Path.Combine(root, "clinic.db");
        File.Copy(AgentTests.SampleDatabase, db);
        var window = new MainWindow();
        window.Show();
        window.SetConnection(DbEngine.Sqlite, $"Data Source={db};Pooling=False");
        try
        {
            Click(window.cmdAddTableSource);
            var dialog = Owned<AddTableSourceDialog>(window);
            for (var i = 0; i < 400 && dialog.lstTables.ItemCount == 0; i++) Settle(1);
            dialog.lstTables.SelectedItem = "main.providers";
            for (var i = 0; i < 400 && dialog.lstColumns.ItemCount == 0; i++) Settle(1);
            Click(dialog.FindControl<Button>("cmdSelectAll") ?? dialog.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Select All"));
            Click(dialog.cmdOk);
            Settle();
            Assert.Single(window.QCanvas.Entities);
            Assert.Contains("FROM main.providers", window.TheQuery.Text);
            Assert.Equal("Add main.providers", window.History.UndoLabel);

            Click(window.cmdToggleDistinct);
            Assert.StartsWith("SELECT DISTINCT", window.TheQuery.Text);

            // Delete the base table: the question, Yes, and the whole query goes.
            var table = window.QCanvas.Entities.First(e => e.Metadata is TableSourceResult);
            typeof(MainWindow).GetMethod("DeleteEntity", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [table]);
            var ask = Owned<Window>(window);
            Click(ask.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Yes"));
            Settle();
            Assert.Empty(window.QCanvas.Entities);

            Click(window.cmdUndo);
            Assert.Equal(2, window.QCanvas.Entities.Count);
            Click(window.cmdUndo);
            Click(window.cmdUndo);
            Assert.Empty(window.QCanvas.Entities);
            Click(window.cmdRedo);
            Assert.Single(window.QCanvas.Entities);
        }
        finally
        {
            window.History.MarkSaved();
            window.Close();
            SqliteConnection.ClearAllPools();
        }
    });
}

/// <summary>The README's AI Chat picture: the window with the chat building a query, on a scripted local model (AQB_SHOTS).</summary>
public sealed class AiChatScreenshot
{
    [Fact]
    public void Picture() => HeadlessApp.Run(() =>
    {
        var dir = Environment.GetEnvironmentVariable("AQB_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var root = Directory.CreateTempSubdirectory("aqb-shot-").FullName;
        var db = Path.Combine(root, "clinic.db");
        File.Copy(AgentTests.SampleDatabase, db);
        var window = new MainWindow { Width = 1500, Height = 860 };
        window.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        window.AgentSettings.ChatProvider = "OpenAICompatible";
        window.AgentSettings.ChatServerUrl = "http://localhost:1234/v1";
        window.AgentSettings.ChatServerModel = "qwen3-8b";
        window.AgentSettings.WorkingFolder = root;
        window.Show();
        window.SetConnection(DbEngine.Sqlite, $"Data Source={db};Pooling=False", "clinic");
        if (!window.ChatHolder.IsVisible) window.ToggleChat();
        var server = new FakeModelServer("http://localhost:1234/v1")
            .Reply(Say("I'll look at the visits table and how it joins to providers."), Call("c1", "describe_table", new { table = "visits" }))
            .Reply(Say("Visits point at providers by provider_id. Building the query: grouped by provider, charges summed, highest first."), Call("c2", "build_query", new
            {
                query = new
                {
                    table = "visits",
                    lookups = new[] { new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name" }, aliases = new Dictionary<string, string> { ["name"] = "Provider" }, join = "INNER" } },
                    group_by = new
                    {
                        fields = new[] { "LOOKUP_1.name" },
                        aggregates = new object[] { new { function = "SUM", field = "charge", alias = "Charges" }, new { function = "COUNT(*)", alias = "Visits" } },
                    },
                    sort = new[] { new { field = "Charges", direction = "DESC" } },
                    limit = 5,
                },
            }))
            .Reply(Call("c3", "check_query", new { }))
            .Reply(Call("c4", "run_query", new { }))
            .Reply(Say("Done: the top 5 providers by total charges, with their visit counts. The database accepted the query and it returned 5 rows, which are in your Results Grid (I see only the count). Ctrl+Z or Undo this reply takes it back."));
        var panel = window.Chat!;
        panel.ServerFactory = (_, key) => server.Client(key);
        panel.Input.Text = "Which 5 providers bill the most? Show their visit counts too.";
        panel.Send();
        AgentTests.Wait(panel.Running.ContinueWith(_ => true));
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, "ai-chat.png"));
        window.History.MarkSaved();
        window.Close();
        SqliteConnection.ClearAllPools();

        // Setup: Claude's API on a local server (LM Studio), with the core tools.
        var second = new MainWindow { Width = 1500, Height = 860 };
        second.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        second.AgentSettings.ChatProvider = "Anthropic";
        second.AgentSettings.ChatAnthropicUrl = "http://localhost:1234";
        second.AgentSettings.ChatAnthropicModel = "qwen/qwen3-8b";
        second.Show();
        if (!second.ChatHolder.IsVisible) second.ToggleChat();
        second.Chat!.SetupOpen = true;
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        second.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ai-chat-setup.png"));
        second.Close();
    });
}
