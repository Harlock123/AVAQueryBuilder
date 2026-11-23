using System.Net.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>
/// The MCP server: <see cref="QueryTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection),
/// with instructions that tell an agent AVA Query Builder's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open window, and in-process for the program's own AI Chat.
/// </summary>
public static class QueryServer
{
    public const string Name = "AVAQueryBuilder";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVA Query Builder builds SELECT queries as a diagram, for SQL Server, PostgreSQL and SQLite: a base table, lookups (joins) to other tables, filters, derived fields, CASE expressions, grouping with counts and sums, sorting, a row limit and DISTINCT, each an entity on the diagram. The program writes the SQL from the diagram in the connected database's own dialect, runs it and shows the rows in its Results Grid. You build queries with the same entities a person uses, so a person can read, check and change what you made.

        Conventions (they matter):
        - The query is connected to one database (get_query, list_connections; connect switches). You read its catalogue (list_tables, describe_table, find_columns: names, types, keys, foreign keys) but never its data, unless the person allows it (below).
        - Tables are "schema.table": dbo.Patients on SQL Server, public.patients on PostgreSQL, main.patients on SQLite. You may write just the table name when it's unambiguous.
        - Fields: a base table's column is "schema.table.column"; a lookup's is "LOOKUP_n.column", n counting the lookups from 1. Tools accept "patients.last_name", or "last_name" when only one table in the query has it; get_query lists the query's exact fields. Derived fields, CASE expressions and aggregates are named by their alias where sorting and grouping want them.
        - A lookup joins the base table's from column(s) to the lookup table's on column: LEFT keeps every base row, INNER only matches. describe_table suggests lookups from the foreign keys. Every lookup hangs off the base table; to reach a table two steps away, make the middle table the base or join both from the base.
        - Filter values are quoted for you: give plain text and numbers ("2026-01-01", "Smith", "42"); LIKE takes % and _; IN takes a list. Each filter's conditions are joined by AND or OR, and separate filters are ANDed: (a OR b) AND c is two filters. Dates compare as text unless cast (cast DATE).
        - Every call that changes the query is one undo step (undo takes it back; in the program Ctrl+Z does too), and a call that fails changes nothing. Each change answers with the SQL it makes.
        - Nothing you do writes to a database: queries are only SELECTs, and they run inside a transaction that is rolled back.
        - Rows are protected data (patients, members, staff). run_query tells you how many rows a query returns and its columns, and shows the rows to the person in the Results Grid; you see the rows only if the person has turned on AI › Let AI Agents See Query Results (get_query's agents_see_rows). Don't ask them to paste rows; work from counts, columns and what they tell you.

        A good way to work: get_query; find the tables (list_tables, find_columns) and read them (describe_table); build the query (build_query does it in one call; add_lookup, add_filter, set_group_by, set_sort and the rest change one part); check_query to have the database check the SQL; run_query to see the count; render_view to show the diagram; explain the query in plain words and say what it returns. Save it (save_query) or export its rows (export_results, .csv or .xlsx) when asked. batch runs several changes as one undo step (dry_run to preview), and save_steps keeps a sequence as a steps file with ${parameters} that run_steps runs again with other values (a monthly report with a different month).

        The server's prompts are saved recipes (a question in plain words to a query, counts by group, records missing a related row, duplicates, top N, a date-range report, explaining a saved query, moving a query to another engine): step-by-step plans with the exact tool calls, to follow or adapt.
        """;

    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IQueryHost host) => Tools(new QueryTools(host));

    /// <summary>The MCP tools of one set of handlers (one session's: its checkpoints and its recording).</summary>
    public static IEnumerable<McpServerTool> Tools(QueryTools target)
    {
        foreach (var method in typeof(QueryTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVA Query Builder",
        Version = typeof(QueryServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IQueryHost host) => Configure(new McpServerOptions(), host);

    /// <summary>Fills a new session's options (ones the network door's HTTP handler made) as <see cref="Options"/> makes them.</summary>
    public static McpServerOptions Configure(McpServerOptions options, IQueryHost host)
    {
        options.ServerInfo = Info;
        options.ServerInstructions = Instructions;
        options.ToolCollection = [];
        var target = new QueryTools(host);
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                var result = await next(request, cancel).ConfigureAwait(false);
                // The session's changes, as steps save_steps can keep: every call that worked and may change something.
                if (tool != null && result.IsError != true && tool.Annotations?.ReadOnlyHint != true)
                    target.Record(tool.Name, request.Params!.Arguments);
                return result;
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(target)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IQueryHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (!tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            // A required argument that's missing: say which, and what it is (the SDK's own words are about dictionaries).
            if (tool.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                var missing = required.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0 && args?.ContainsKey(r) != true).ToList();
                if (missing.Count > 0)
                    return $"{tool.Name} needs {string.Join(" and ", missing)}: " + string.Join(" ", missing.Select(m =>
                        props.TryGetProperty(m, out var p) && p.TryGetProperty("description", out var d) ? $"{m} is {d.GetString()?.TrimEnd('.')}." : $"{m}."));
            }
            if (args == null) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IQueryHost
    {
        public string Root => Path.GetTempPath();
        public IQueryLibrary Library => throw new InvalidOperationException("Not connected to a query.");
        public ConnectionStore Connections => new(null);
        public bool Live => false;
        public Task<T> RunAsync<T>(string action, Func<IQuerySession, Task<T>> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected to a query.");
    }
}
