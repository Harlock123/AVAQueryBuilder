using Avalonia;
using Avalonia.Headless;
using AVAQueryBuilder;

[assembly: AvaloniaTestApplication(typeof(AVAQueryBuilder.Tests.HeadlessApp))]
// One app for the whole run: resetting between tests leaves a render queued after its fonts are gone.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace AVAQueryBuilder.Tests;

/// <summary>The app, rendered by Skia off screen: dialogs can be driven and pictured without opening a window.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs <paramref name="action"/> on the headless UI thread.</summary>
    public static void Run(Action action) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly)
            .Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}

public sealed class ConnectionDialogTests
{
    /// <summary>Lets queued UI work run: TextBox raises TextChanged on a later dispatcher pass.</summary>
    private static void Settle() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    [Fact]
    public void Each_engine_shows_its_own_fields_and_builds_its_own_connection_string() => HeadlessApp.Run(() =>
    {
        var dialog = new ConnectionStringDialog(DbEngine.SqlServer);
        dialog.Show();

        Assert.True(dialog.pnlAuth.IsVisible);
        Assert.False(dialog.txtUsername.IsEnabled);
        dialog.cboServer.Text = "sql.example";
        Settle();
        dialog.cboAuth.SelectedIndex = 1;
        Settle();
        dialog.txtUsername.Text = "me";
        Settle();
        Assert.Contains("Data Source=sql.example", dialog.txtConnectionString.Text);
        Assert.Contains("User ID=me", dialog.txtConnectionString.Text);

        dialog.cboEngine.SelectedIndex = 1;
        Settle();
        Assert.False(dialog.pnlAuth.IsVisible);
        Assert.True(dialog.pnlCredentials.IsVisible);
        Assert.True(dialog.txtUsername.IsEnabled);
        Assert.False(dialog.cmdBrowseServers.IsVisible);
        Assert.Equal("Host (host or host:port):", dialog.lblServer.Text);
        dialog.cboServer.Text = "pg.example:6543";
        Settle();
        dialog.txtUsername.Text = "ana";
        Settle();
        Assert.Contains("Host=pg.example", dialog.txtConnectionString.Text);
        Assert.Contains("Port=6543", dialog.txtConnectionString.Text);
        Assert.Contains("Username=ana", dialog.txtConnectionString.Text);

        dialog.cboEngine.SelectedIndex = 2;
        Settle();
        Assert.False(dialog.pnlCredentials.IsVisible);
        Assert.False(dialog.pnlDatabase.IsVisible);
        Assert.Equal("Choose File...", dialog.cmdBrowseServers.Content);
        dialog.cboServer.Text = "/data/shop.db";
        Settle();
        dialog.txtUsername.Text = "ignored";
        Settle();
        Assert.Equal("Data Source=/data/shop.db;Mode=ReadWrite", dialog.txtConnectionString.Text);

        dialog.Close();
    });

    [Fact]
    public void Opens_on_the_engine_already_in_use() => HeadlessApp.Run(() =>
    {
        var dialog = new ConnectionStringDialog(DbEngine.Sqlite);
        Assert.Equal(2, dialog.cboEngine.SelectedIndex);
        Assert.Equal("Database File:", dialog.lblServer.Text);
    });

    /// <summary>Writes the dialog for each engine to AQB_SHOTS (when set), to look at the layout.</summary>
    [Fact]
    public void Pictures() => HeadlessApp.Run(() =>
    {
        var dir = Environment.GetEnvironmentVariable("AQB_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var engine in new[] { DbEngine.SqlServer, DbEngine.PostgreSql, DbEngine.Sqlite })
        {
            var dialog = new ConnectionStringDialog(engine);
            dialog.Show();
            dialog.cboServer.Text = engine switch
            {
                DbEngine.PostgreSql => "localhost:5432",
                DbEngine.Sqlite => "/home/me/data/shop.db",
                _ => "localhost,1433"
            };
            dialog.txtUsername.Text = engine == DbEngine.PostgreSql ? "postgres" : "";
            dialog.cboDatabase.Text = engine == DbEngine.PostgreSql ? "shop" : "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"connect-{engine}.png"));
            dialog.Close();
        }
    });
}
