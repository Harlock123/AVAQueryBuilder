using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AVAQueryBuilder.Agents.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAQueryBuilder.Agents.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What the model is told: the MCP server's own instructions, and a few lines about working inside the program.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVA Query Builder itself, in its AI Chat panel, with the person watching. Your tool calls change the query on the diagram in the window as you make them, and the person's Ctrl+Z undoes your calls one at a time. Talk to them briefly and plainly: say what you're about to build, build it, check it (check_query, then run_query for the count), then say in plain words what the query returns. Keep it to a few sentences; the tool calls show in the panel on their own. Read the database's structure before building (find_columns, describe_table); never guess table or column names. The rows a query returns appear in the person's Results Grid; you see them only if they have allowed it. If the person is busy in the program (a dialog open), calls wait for them. Files you read, open, save or export are in the working folder.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => QueryServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_view still shows the person the diagram, but check your work yourself with get_query (every entity and the SQL) and check_query.";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: the query, the database's structure,
    /// building it in one call or a part at a time, checking and running it, and undo.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "get_query", "list_tables", "describe_table", "find_columns", "build_query", "add_lookup", "add_filter",
        "set_group_by", "set_sort", "set_limit", "remove", "check_query", "run_query", "render_view", "save_query", "undo",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
