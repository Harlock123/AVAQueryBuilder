using System.IO.Pipelines;
using AVAQueryBuilder.Agents;
using AVAQueryBuilder.Agents.Mcp;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAQueryBuilder.Tests.Agents;

/// <summary>The window's doors for outside agents: the pipe for <c>AVAQueryBuilder --mcp --attach</c>, and the network door.</summary>
public sealed class LiveAgentTests
{
    private static MainWindow Open()
    {
        var window = new MainWindow { AgentPipe = "avaquerybuilder-test-" + Guid.NewGuid().ToString("N") };
        window.Show();
        return window;
    }

    private static void Close(MainWindow window)
    {
        window.History.MarkSaved();
        window.Close();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void An_outside_agent_attaches_through_the_pipe_and_builds_in_the_window() => HeadlessApp.Run(() =>
    {
        var w = Open();
        var root = Directory.CreateTempSubdirectory("aqb-root-").FullName;
        var db = Path.Combine(root, "clinic.db");
        File.Copy(AgentTests.SampleDatabase, db);
        w.SetConnection(DbEngine.Sqlite, $"Data Source={db};Pooling=False");
        Assert.StartsWith("AI agents may connect", w.SetAllowAgents(true));
        Assert.True(LiveListener.Answers(w.AgentPipe));
        var bridge = new LiveBridge(w.AgentPipe, root);
        Pipe up = new(), down = new();
        var server = McpServer.Create(new StreamServerTransport(up.Reader.AsStream(), down.Writer.AsStream(), "attach"), bridge.Options());
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => server.RunAsync(stop.Token));
        var client = AgentTests.Wait(McpClient.CreateAsync(new StreamClientTransport(up.Writer.AsStream(), down.Reader.AsStream())));
        try
        {
            Assert.Contains("live app", client.ServerInstructions);
            var result = AgentTests.Wait(client.CallToolAsync("build_query", new Dictionary<string, object?> { ["table"] = "providers", ["columns"] = new[] { "name" } }).AsTask());
            Assert.False(result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            Assert.Single(w.QCanvas.Entities);
            Assert.Contains("main.providers", w.TheQuery.Text);
        }
        finally
        {
            // Everything this test started ends with it: work left running would reach into the next test's app.
            AgentTests.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => true));
            stop.Cancel();
            AgentTests.Wait(serving.ContinueWith(_ => true));
            AgentTests.Wait(bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            Assert.Equal("AI agents are shut out.", w.SetAllowAgents(false));
            AgentTests.Wait(w.Agents.StopAsync().ContinueWith(_ => true));
            Close(w);
        }
    });

    [Fact]
    public void The_network_door_opens_on_the_settings_and_wants_the_token() => HeadlessApp.Run(() =>
    {
        var w = Open();
        try
        {
            w.AgentSettings.AllowNetworkAgents = true;
            w.AgentSettings.NetworkAgentsAddress = "localhost";
            w.AgentSettings.NetworkAgentsPort = 0;
            w.AgentSettings.NetworkAgentsFolder = Directory.CreateTempSubdirectory("aqb-net-").FullName;
            Assert.StartsWith("AI agents on other computers may connect", AgentTests.Wait(w.SyncNetworkAsync()));
            using var http = new HttpClient();
            var refused = AgentTests.Wait(http.PostAsync(w.Agents.NetworkUrls.First(), new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
            w.AgentSettings.Save();
            Assert.DoesNotContain(w.NetworkToken, File.ReadAllText(AgentSettings.Path));
        }
        finally
        {
            w.AgentSettings.AllowNetworkAgents = false;
            AgentTests.Wait(w.SyncNetworkAsync());
            Close(w);
        }
    });
}
