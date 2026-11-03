using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace AVAQueryBuilder;

public partial class ConnectionStringDialog : Window
{
    private readonly ObservableCollection<string> _servers = new();
    private readonly ObservableCollection<string> _databases = new();

    public string ResultConnectionString { get; private set; } = string.Empty;
    public DbEngine ResultEngine { get; private set; } = DbEngine.SqlServer;

    public ConnectionStringDialog() : this(DbEngine.SqlServer)
    {
    }

    public ConnectionStringDialog(DbEngine engine)
    {
        InitializeComponent();
        cboServer.ItemsSource = _servers;
        cboDatabase.ItemsSource = _databases;
        cboServer.SelectionChanged += CboServer_SelectionChanged;
        cboDatabase.SelectionChanged += CboDatabase_SelectionChanged;
        cboEngine.SelectedIndex = (int)engine;
        ApplyEngine();
    }

    private DbEngine Engine => cboEngine.SelectedIndex switch
    {
        1 => DbEngine.PostgreSql,
        2 => DbEngine.Sqlite,
        _ => DbEngine.SqlServer
    };

    /// <summary>Shows the fields the chosen engine uses and labels the server box for it.</summary>
    private void ApplyEngine()
    {
        var engine = Engine;
        lblServer.Text = engine switch
        {
            DbEngine.PostgreSql => "Host (host or host:port):",
            DbEngine.Sqlite => "Database File:",
            _ => "Server Name:"
        };
        cmdBrowseServers.Content = engine == DbEngine.Sqlite ? "Choose File..." : "Browse...";
        cmdBrowseServers.IsVisible = engine != DbEngine.PostgreSql;
        pnlAuth.IsVisible = engine == DbEngine.SqlServer;
        pnlCredentials.IsVisible = engine != DbEngine.Sqlite;
        pnlDatabase.IsVisible = engine != DbEngine.Sqlite;

        var credentials = engine == DbEngine.PostgreSql || cboAuth.SelectedIndex == 1;
        txtUsername.IsEnabled = credentials;
        txtPassword.IsEnabled = credentials;

        _servers.Clear();
        _databases.Clear();
        txtStatus.Text = string.Empty;
        UpdateConnectionStringFromFields();
    }

    private string BuildConnectionString()
    {
        var server = (cboServer.SelectedItem as string ?? cboServer.Text ?? string.Empty).Trim();
        var database = (cboDatabase.SelectedItem as string ?? cboDatabase.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(server))
            return string.Empty;

        switch (Engine)
        {
            case DbEngine.Sqlite:
                // ReadWrite, not the default ReadWriteCreate: a mistyped path fails instead of making an empty file.
                return new SqliteConnectionStringBuilder { DataSource = server, Mode = SqliteOpenMode.ReadWrite }
                    .ConnectionString;

            case DbEngine.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder();
                var colon = server.LastIndexOf(':');
                if (colon > 0 && int.TryParse(server[(colon + 1)..], out var port))
                {
                    builder.Host = server[..colon];
                    builder.Port = port;
                }
                else
                {
                    builder.Host = server;
                }
                if (!string.IsNullOrWhiteSpace(database))
                    builder.Database = database;
                builder.Username = txtUsername.Text ?? string.Empty;
                builder.Password = txtPassword.Text ?? string.Empty;
                return builder.ConnectionString;
            }

            default:
            {
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = server,
                    TrustServerCertificate = true
                };

                if (!string.IsNullOrWhiteSpace(database))
                    builder.InitialCatalog = database;

                if (cboAuth.SelectedIndex == 0)
                {
                    builder.IntegratedSecurity = true;
                }
                else
                {
                    builder.IntegratedSecurity = false;
                    builder.UserID = txtUsername.Text ?? string.Empty;
                    builder.Password = txtPassword.Text ?? string.Empty;
                }

                return builder.ConnectionString;
            }
        }
    }

    private void UpdateConnectionStringFromFields()
    {
        txtConnectionString.Text = BuildConnectionString();
    }

    private void CboEngine_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (txtUsername == null) return;
        ApplyEngine();
    }

    private void CboAuth_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (txtUsername == null) return;
        var isSqlAuth = cboAuth.SelectedIndex == 1;
        txtUsername.IsEnabled = isSqlAuth;
        txtPassword.IsEnabled = isSqlAuth;
        if (!isSqlAuth)
        {
            txtUsername.Text = string.Empty;
            txtPassword.Text = string.Empty;
        }
        UpdateConnectionStringFromFields();
    }

    private void Field_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (txtConnectionString == null) return;
        UpdateConnectionStringFromFields();
    }

    private async void CmdBrowseServers_Click(object? sender, RoutedEventArgs e)
    {
        if (Engine == DbEngine.Sqlite)
            await ChooseSqliteFileAsync();
        else
            await BrowseSqlServersAsync();
    }

    private async Task ChooseSqliteFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQLite Database",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Databases") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3", "*.db3" } },
                FilePickerFileTypes.All
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;

        _servers.Clear();
        _servers.Add(path);
        cboServer.SelectedIndex = 0;
        UpdateConnectionStringFromFields();
    }

    private async Task BrowseSqlServersAsync()
    {
        txtStatus.Text = "Searching for SQL Server instances on the network...";
        cmdBrowseServers.IsEnabled = false;

        try
        {
            var servers = await Task.Run(() =>
            {
                var list = new List<string>();
                try
                {
                    // SQL Server Browser uses UDP port 1434
                    using var udpClient = new UdpClient();
                    udpClient.Client.ReceiveTimeout = 3000;
                    udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

                    var requestPayload = new byte[] { 0x02 };
                    udpClient.Send(requestPayload, requestPayload.Length, new IPEndPoint(IPAddress.Broadcast, 1434));

                    try
                    {
                        while (true)
                        {
                            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                            var response = udpClient.Receive(ref remoteEndPoint);
                            var responseStr = System.Text.Encoding.ASCII.GetString(response);
                            ParseBrowserResponse(responseStr, list);
                        }
                    }
                    catch (SocketException)
                    {
                        // Timeout - done receiving
                    }
                }
                catch
                {
                    // Browser service may not be available
                }
                return list;
            });

            _servers.Clear();
            if (servers.Count > 0)
            {
                foreach (var s in servers)
                    _servers.Add(s);
                cboServer.SelectedIndex = 0;
                txtStatus.Text = $"Found {servers.Count} server(s).";
            }
            else
            {
                txtStatus.Text = "No servers found. You can type a server name manually.";
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Browse failed: {ex.Message}";
        }
        finally
        {
            cmdBrowseServers.IsEnabled = true;
        }

        UpdateConnectionStringFromFields();
    }

    private static void ParseBrowserResponse(string response, List<string> list)
    {
        // SQL Browser response format: semicolon-delimited key-value pairs
        var parts = response.Split(';');
        string? serverName = null;
        string? instanceName = null;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("ServerName", StringComparison.OrdinalIgnoreCase))
                serverName = parts[i + 1];
            else if (parts[i].Equals("InstanceName", StringComparison.OrdinalIgnoreCase))
                instanceName = parts[i + 1];

            if (serverName != null && instanceName != null)
            {
                var entry = string.IsNullOrWhiteSpace(instanceName)
                    ? serverName
                    : $"{serverName}\\{instanceName}";
                if (!string.IsNullOrWhiteSpace(entry) && !list.Contains(entry))
                    list.Add(entry);
                serverName = null;
                instanceName = null;
            }
        }
    }

    private async void CmdRefreshDatabases_Click(object? sender, RoutedEventArgs e)
    {
        await FetchDatabasesAsync();
    }

    private async Task FetchDatabasesAsync()
    {
        if (Engine == DbEngine.Sqlite) return;

        var connStr = BuildConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            txtStatus.Text = "Enter a server name first.";
            return;
        }

        txtStatus.Text = "Querying server for databases...";
        cmdRefreshDatabases.IsEnabled = false;

        try
        {
            var engine = Engine;
            var databases = await Task.Run(() => Db.ListDatabases(engine, connStr));

            _databases.Clear();
            foreach (var db in databases)
                _databases.Add(db);

            if (databases.Count > 0)
            {
                cboDatabase.SelectedIndex = 0;
                txtStatus.Text = $"Found {databases.Count} database(s).";
            }
            else
            {
                txtStatus.Text = "No databases found.";
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Failed to fetch databases: {ex.Message}";
        }
        finally
        {
            cmdRefreshDatabases.IsEnabled = true;
        }

        UpdateConnectionStringFromFields();
    }

    private async void CboServer_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (cboServer.SelectedItem == null) return;
        UpdateConnectionStringFromFields();
        await FetchDatabasesAsync();
    }

    private void CboDatabase_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateConnectionStringFromFields();
    }

    private async void CmdTestConnection_Click(object? sender, RoutedEventArgs e)
    {
        var connStr = txtConnectionString.Text;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            txtStatus.Text = "No connection string to test.";
            return;
        }

        txtStatus.Text = "Testing connection...";
        cmdTestConnection.IsEnabled = false;

        try
        {
            var engine = Engine;
            await Task.Run(() => Db.Test(engine, connStr));
            txtStatus.Text = "Connection successful!";
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            cmdTestConnection.IsEnabled = true;
        }
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        ResultConnectionString = txtConnectionString.Text ?? string.Empty;
        ResultEngine = Engine;
        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        ResultConnectionString = string.Empty;
        Close(false);
    }
}
