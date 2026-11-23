using Avalonia.Threading;
using AVAQueryBuilder.Agents.Mcp;

namespace AVAQueryBuilder.Agents;

/// <summary>The query open in the window, as an agent works on it: the person's own query, history, connection and grid.</summary>
internal sealed class WindowSession(MainWindow window) : IQuerySession
{
    public QueryFile Document => window.Snapshot();

    public QueryHistory History => window.History;

    public string? FilePath => window.CurrentFilePath;

    public DbEngine Engine => AppState.Engine;

    public string ConnectionString => AppState.ConnectionString;

    public string? ConnectionName => AppState.ConnectionName;

    public bool AgentsSeeRows => window.AgentSettings.AgentsSeeRows;

    public void Apply(QueryFile query, string label) => window.Commit(query, label);

    public void Refresh() => window.ShowQuery();

    public void Replace(QueryFile query, string? path) => window.Adopt(query, path);

    public void Saved(string path) => window.AgentSaved(path);

    public void Connect(DbEngine engine, string connectionString, string? name) => window.SetConnection(engine, connectionString, name);

    public void ShowRows(QueryRows rows, string sql) => window.ShowAgentRows(rows);
}

/// <summary>
/// AI agents in the open window: the session and host the chat and outside agents share, the door on this computer
/// (a pipe, for <c>AVAQueryBuilder --mcp --attach</c>) and the network door (MCP over HTTP with a token).
/// </summary>
internal sealed class LiveAgents(MainWindow window) : IQueryDesk, IUiThread, IAsyncDisposable
{
    private readonly WindowSession _session = new(window);
    private LiveListener? _listener;
    private NetworkListener? _network;

    public QueryLibrary Library { get; } = new(window.AgentStage);

    /// <summary>The person's database connections (connections.json beside the settings; tests give their own).</summary>
    public ConnectionStore Connections { get; set; } = new(Path.Combine(AgentSettings.Folder, ConnectionStore.FileName));

    public bool Running => _listener != null;

    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How many outside agents are connected (pipe and network together), when it changes.</summary>
    public event Action<int>? ConnectionsChanged;

    public IQuerySession Session => _session;

    public string? Busy => window.OwnedWindows.Count > 0 || ChatLook.ModalsOpen > 0 ? "a dialog box is open in AVA Query Builder" : null;

    public void Report(string text) => window.Say(text);

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);

    /// <summary>The host outside agents work through, in <paramref name="root"/>.</summary>
    public QueryHost Host(string root) => new(this, this, Library, root, Connections) { BusyWait = BusyWait, Live = true };

    /// <summary>The host the AI Chat runs its in-process MCP server on; its status lines name the chat's model.</summary>
    public QueryHost ChatHost(string root, Func<string> who) => new(this, new ChatDesk(this, who), Library, root, Connections) { BusyWait = BusyWait, Live = true };

    private sealed class ChatDesk(LiveAgents agents, Func<string> who) : IQueryDesk
    {
        public IQuerySession Session => agents.Session;

        public string? Busy => agents.Busy;

        public void Report(string text) =>
            agents.Report(text.StartsWith("AI agent: ", StringComparison.Ordinal) ? who() + ": " + text["AI agent: ".Length..] : text);
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another AVA Query Builder has it), or null.</summary>
    public string? Start(string pipeName, Func<string> root)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, asked => Host(Directory.Exists(asked) ? asked : root()));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        listener.Stop();
        await listener.DisposeAsync();
    }

    public bool NetworkRunning => _network != null;

    public NetworkSettings? NetworkSettings { get; private set; }

    public string? NetworkRoot { get; private set; }

    public IReadOnlyList<string> NetworkUrls => _network?.Urls ?? [];

    public async Task<string?> StartNetworkAsync(NetworkSettings settings, string root)
    {
        if (_network != null) return null;
        if (!Directory.Exists(root)) return $"the working folder {root} doesn't exist.";
        var listener = new NetworkListener(settings, QueryServer.Name, o => QueryServer.Configure(o, Host(root)));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            await listener.StartAsync();
        }
        catch (IOException e)
        {
            return e.Message;
        }
        _network = listener;
        NetworkSettings = settings;
        NetworkRoot = root;
        return null;
    }

    public async Task StopNetworkAsync()
    {
        var listener = _network;
        _network = null;
        NetworkSettings = null;
        NetworkRoot = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await StopNetworkAsync();
    }
}
