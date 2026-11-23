using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>A named database connection: which engine, and how to reach it.</summary>
public sealed class DataConnection
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string? Note { get; set; }

    public DbEngine Engine => Db.ParseEngine(Provider);

    /// <summary>The connection string with its secrets taken out, for showing: "Host=db1;Database=clinic;Username=reader".</summary>
    public string Redacted => ConnectionStore.Redact(ConnectionString);
}

/// <summary>
/// The database connections the person has set up, by name, kept in <c>connections.json</c> beside the settings,
/// readable by its owner only (on Linux and macOS). Agents refer to a connection by its name and see its engine and
/// server, never its password; connections an agent adds for a session (headless) live in memory unless saved.
/// </summary>
public sealed class ConnectionStore(string? path)
{
    public const string FileName = "connections.json";

    private readonly List<DataConnection> _session = [];
    private readonly Lock _lock = new();

    /// <summary>The file (null: memory only, as in tests).</summary>
    public string? Path { get; } = path;

    public IReadOnlyList<DataConnection> All
    {
        get
        {
            lock (_lock)
            {
                var saved = Load();
                return saved.Concat(_session.Where(s => saved.All(x => !x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
            }
        }
    }

    public DataConnection Get(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(All.Count == 0
            ? $"There's no database connection \"{name}\": none are set up. The person adds them in AI › Database Connections (or add_connection takes one for this session)."
            : $"There's no database connection \"{name}\". The connections: {string.Join(", ", All.Select(c => c.Name))}.");

    /// <summary>Adds or replaces a connection; <paramref name="save"/> keeps it in the file for next time.</summary>
    public void Put(DataConnection connection, bool save)
    {
        if (string.IsNullOrWhiteSpace(connection.Name)) throw new ArgumentException("A connection needs a name.");
        if (string.IsNullOrWhiteSpace(connection.ConnectionString)) throw new ArgumentException("A connection needs a connection string.");
        connection.Name = connection.Name.Trim();
        connection.Provider = Db.ParseEngine(connection.Provider).ToString();
        lock (_lock)
        {
            _session.RemoveAll(c => c.Name.Equals(connection.Name, StringComparison.OrdinalIgnoreCase));
            if (save && Path != null)
            {
                var list = Load();
                list.RemoveAll(c => c.Name.Equals(connection.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(connection);
                Write(list);
            }
            else
            {
                _session.Add(connection);
            }
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            var removed = _session.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (Path == null) return removed;
            var list = Load();
            if (list.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) return removed;
            Write(list);
            return true;
        }
    }

    private List<DataConnection> Load()
    {
        if (Path == null || !File.Exists(Path)) return [];
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
            return (root?["connections"] as JsonArray ?? []).OfType<JsonObject>().Select(o => new DataConnection
            {
                Name = (string?)o["name"] ?? "",
                Provider = (string?)o["provider"] ?? "",
                ConnectionString = (string?)o["connection_string"] ?? "",
                Note = (string?)o["note"],
            }).Where(c => c.Name.Length > 0).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(List<DataConnection> list)
    {
        var root = new JsonObject
        {
            ["connections"] = new JsonArray(list.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["provider"] = c.Provider, ["connection_string"] = c.ConnectionString, ["note"] = c.Note,
            }).ToArray()),
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path!)!);
        if (File.Exists(Path)) File.Delete(Path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(Path!, options);
        using var writer = new StreamWriter(stream);
        writer.Write(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static readonly string[] SecretKeys = ["password", "pwd", "user password", "accesstoken", "access token", "authentication key", "token", "client secret"];

    /// <summary>A connection string without its passwords and tokens, otherwise as written.</summary>
    public static string Redact(string connectionString)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in connectionString)
        {
            if (quote is null && c is '\'' or '"') quote = c;
            else if (quote == c) quote = null;
            if (c == ';' && quote is null)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString());
        var kept = parts.Where(p => p.Trim().Length > 0).Where(p =>
        {
            var key = (p.Contains('=') ? p[..p.IndexOf('=')] : p).Trim().ToLowerInvariant();
            return !SecretKeys.Contains(key) && !key.Contains("secret") && !key.Contains("password") && !key.Contains("pwd");
        });
        return string.Join(";", kept.Select(p => p.Trim()));
    }
}
