using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace AVAQueryBuilder;

public partial class ConnectionStringDialog : Window
{
    private readonly ObservableCollection<string> _servers = new();
    private readonly ObservableCollection<string> _databases = new();

    public string ResultConnectionString { get; private set; } = string.Empty;

    public ConnectionStringDialog()
    {
        InitializeComponent();
        cboServer.ItemsSource = _servers;
        cboDatabase.ItemsSource = _databases;
        cboServer.SelectionChanged += CboServer_SelectionChanged;
        cboDatabase.SelectionChanged += CboDatabase_SelectionChanged;
    }

    private string BuildConnectionString()
    {
        var server = cboServer.SelectedItem as string ?? cboServer.Text ?? string.Empty;
        var database = cboDatabase.SelectedItem as string ?? cboDatabase.Text ?? string.Empty;
        var useWindowsAuth = cboAuth.SelectedIndex == 0;

        if (string.IsNullOrWhiteSpace(server))
            return string.Empty;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            TrustServerCertificate = true
        };

        if (!string.IsNullOrWhiteSpace(database))
            builder.InitialCatalog = database.Trim();

        if (useWindowsAuth)
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

    private void UpdateConnectionStringFromFields()
    {
        txtConnectionString.Text = BuildConnectionString();
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

    private void TxtConnectionString_TextChanged(object? sender, TextChangedEventArgs e)
    {
    }

    private async void CmdBrowseServers_Click(object? sender, RoutedEventArgs e)
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
            var builder = new SqlConnectionStringBuilder(connStr)
            {
                InitialCatalog = "master",
                ConnectTimeout = 10
            };

            var databases = await Task.Run(() =>
            {
                var list = new List<string>();
                using var conn = new SqlConnection(builder.ConnectionString);
                conn.Open();
                using var cmd = new SqlCommand(
                    "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name",
                    conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(reader.GetString(0));
                return list;
            });

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
            var builder = new SqlConnectionStringBuilder(connStr)
            {
                ConnectTimeout = 10
            };

            await Task.Run(() =>
            {
                using var conn = new SqlConnection(builder.ConnectionString);
                conn.Open();
            });

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
        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        ResultConnectionString = string.Empty;
        Close(false);
    }
}
