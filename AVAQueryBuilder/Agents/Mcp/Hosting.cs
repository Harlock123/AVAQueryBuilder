using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>What the server needs from the program that it can't do itself: drawing a query's diagram, which needs Avalonia.</summary>
public interface IQueryLibrary
{
    /// <summary>The query's diagram drawn as a PNG, at <paramref name="scale"/>, with a margin round it.</summary>
    byte[] Picture(QueryFile query, double scale);
}

/// <summary>
/// The query an agent works on and what goes with it: its undo history, its file, and the database it runs against.
/// Used on the host's thread only (<see cref="IQueryHost.RunAsync{T}"/>).
/// </summary>
public interface IQuerySession
{
    /// <summary>The query as it is now (a copy: change it, then <see cref="Apply"/> it).</summary>
    QueryFile Document { get; }

    /// <summary>The query's undo history, the person's own (Ctrl+Z undoes an agent's steps too).</summary>
    QueryHistory History { get; }

    /// <summary>The .qry file the query was opened from or saved to, or null.</summary>
    string? FilePath { get; }

    /// <summary>The engine the SQL is written for (the connected database's).</summary>
    DbEngine Engine { get; }

    /// <summary>The connection string, for running and reading the catalogue. Never shown to an agent.</summary>
    string ConnectionString { get; }

    /// <summary>The saved connection's name, if the database was chosen by name.</summary>
    string? ConnectionName { get; }

    /// <summary>Whether the person lets agents see the rows a query returns (AI › Let AI Agents See Query Results).</summary>
    bool AgentsSeeRows { get; }

    /// <summary>Makes <paramref name="query"/> the query, as one undo step named <paramref name="label"/>.</summary>
    void Apply(QueryFile query, string label);

    /// <summary>Shows the history's current query (after undo, redo or a batch folded into one step).</summary>
    void Refresh();

    /// <summary>Adopts another query (new, open): a fresh history, and not changed.</summary>
    void Replace(QueryFile query, string? path);

    /// <summary>The query was written to <paramref name="path"/>, which becomes its file.</summary>
    void Saved(string path);

    /// <summary>Connects to a database: the SQL is written for its engine from now on.</summary>
    void Connect(DbEngine engine, string connectionString, string? name);

    /// <summary>Shows a run's rows to the person (the results grid in the window); headless, nothing.</summary>
    void ShowRows(QueryRows rows, string sql);
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="QueryTools"/>) only touch the query through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server and the open window.
/// </summary>
public interface IQueryHost
{
    /// <summary>The folder relative paths start from; open, save, export and reading files stay inside it.</summary>
    string Root { get; }

    IQueryLibrary Library { get; }

    /// <summary>The database connections the person has set up, by name.</summary>
    ConnectionStore Connections { get; }

    /// <summary>Working in the open window, with the person watching (so deleting most of a query asks them first).</summary>
    bool Live { get; }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread that owns the query, one call at a time, and returns what it returns
    /// (or throws what it throws). <paramref name="action"/> names it for the status bar. Database work doesn't belong
    /// in it: it would hold up the window.
    /// </summary>
    Task<T> RunAsync<T>(string action, Func<IQuerySession, Task<T>> work, CancellationToken cancel = default);
}

public static class QueryHostExtensions
{
    public static Task<T> RunAsync<T>(this IQueryHost host, string action, Func<IQuerySession, T> work, CancellationToken cancel = default) =>
        host.RunAsync(action, s => Task.FromResult(work(s)), cancel);
}

/// <summary>Runs work on a UI thread (the program's Avalonia dispatcher, or a test's).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The program as the host sees it: the query, whether it can take a call now, and the status bar.</summary>
public interface IQueryDesk
{
    IQuerySession Session { get; }

    /// <summary>Why the program can't take a call just now ("a dialog box is open"), or null. The call waits.</summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did.</summary>
    void Report(string text);
}

/// <summary>
/// The host: calls run against the desk's query on its UI thread, one at a time, so the diagram redraws and Ctrl+Z undoes
/// them like the person's own edits. While the person is busy a call waits up to <see cref="BusyWait"/>.
/// </summary>
public sealed class QueryHost(IUiThread ui, IQueryDesk desk, IQueryLibrary library, string root, ConnectionStore connections) : IQueryHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root { get; } = Path.GetFullPath(root);

    public IQueryLibrary Library { get; } = library;

    public ConnectionStore Connections { get; } = connections;

    public bool Live { get; init; }

    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<IQuerySession, Task<T>> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(async () =>
                {
                    if (desk.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = await work(desk.Session);
                        desk.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        desk.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new QueryBusyException($"AVA Query Builder is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class QueryBusyException(string message) : Exception(message);

/// <summary>A query with no window round it, as <c>AVAQueryBuilder --mcp</c> serves it.</summary>
public sealed class HeadlessSession(bool agentsSeeRows = false) : IQuerySession
{
    public QueryHistory History { get; } = new(new QueryFile());

    public QueryFile Document => History.Current;

    public string? FilePath { get; private set; }

    public DbEngine Engine { get; private set; } = DbEngine.Sqlite;

    public string ConnectionString { get; private set; } = "";

    public string? ConnectionName { get; private set; }

    public bool AgentsSeeRows { get; set; } = agentsSeeRows;

    public void Apply(QueryFile query, string label) => History.Commit(query, label);

    public void Refresh() { }

    public void Replace(QueryFile query, string? path)
    {
        History.Reset(query);
        FilePath = path;
    }

    public void Saved(string path)
    {
        FilePath = path;
        History.MarkSaved();
    }

    public void Connect(DbEngine engine, string connectionString, string? name)
    {
        Engine = engine;
        ConnectionString = connectionString;
        ConnectionName = name;
    }

    public void ShowRows(QueryRows rows, string sql) { }
}

public sealed class HeadlessDesk(TextWriter? log = null, bool agentsSeeRows = false) : IQueryDesk
{
    public IQuerySession Session { get; } = new HeadlessSession(agentsSeeRows);

    public string? Busy => null;

    public void Report(string text) => log?.WriteLine(text);
}
