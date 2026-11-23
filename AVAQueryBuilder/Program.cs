using Avalonia;
using System;

namespace AVAQueryBuilder;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args) =>
        // AVAQueryBuilder --mcp: the MCP server for AI agents, with no window (see Agents/AgentMain).
        Agents.Mcp.McpCommandLine.Wanted(args) ? Agents.AgentMain.Run(args) : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
