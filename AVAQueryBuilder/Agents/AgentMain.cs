using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AVAQueryBuilder.Agents.Mcp;

namespace AVAQueryBuilder.Agents;

/// <summary>Runs work on Avalonia's UI thread, where the diagram is drawn.</summary>
internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// <c>AVAQueryBuilder --mcp</c>: the MCP server with no window. Avalonia starts on its headless platform with Skia (so the
/// diagram draws exactly as in the window, in a hidden window of its own), its dispatcher runs on this thread, and the
/// server runs beside it until the client goes away or Ctrl+C.
/// </summary>
internal static class AgentMain
{
    public static int Run(string[] args)
    {
        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var stage = new Panel { Width = 0, Height = 0, ClipToBounds = true };
        new Window { Content = stage, Width = 10, Height = 10 }.Show();
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var connections = new ConnectionStore(Path.Combine(AgentSettings.Folder, ConnectionStore.FileName));
        var library = new QueryLibrary(stage);
        var server = Task.Run(() => McpCommandLine.RunAsync(args, () => library, connections, new AvaloniaThread(), input, output, Console.Error, stop.Token));
        server.ContinueWith(_ => Dispatcher.UIThread.Post(() => stop.Cancel()), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(stop.Token);
        try
        {
            return server.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"The MCP server stopped: {e.Message}");
            return 1;
        }
    }

    /// <summary>The app for headless work: the Fluent theme, and no main window.</summary>
    private sealed class HeadlessApp : Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }
}
