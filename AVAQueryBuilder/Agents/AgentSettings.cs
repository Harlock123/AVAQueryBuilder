using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAQueryBuilder.Agents;

/// <summary>
/// The AI side's settings: outside agents and the network door, and the AI Chat's model. Kept in <c>agents.json</c>
/// in the program's settings folder (<c>AVAQueryBuilder</c> under the user's application data); tests point <see cref="FolderOverride"/> at a folder of their own. API
/// keys and the network token are never here: they live in owner-only files of their own beside it.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Whether AI agents outside the program may work in it (AI › Allow AI Agents). Off until turned on.</summary>
    public bool AllowAgents { get; set; }

    /// <summary>Whether agents on other computers may, over the network (AI › Connect an Agent). Off until turned on.</summary>
    public bool AllowNetworkAgents { get; set; }
    public string NetworkAgentsAddress { get; set; } = "0.0.0.0";
    public int NetworkAgentsPort { get; set; } = 8771;
    /// <summary>The network door's working folder (empty: the Documents folder).</summary>
    public string NetworkAgentsFolder { get; set; } = "";
    public string NetworkAgentsCertificate { get; set; } = "";
    public string NetworkAgentsKey { get; set; } = "";

    /// <summary>The folder the chat (and agents attached to the window) read, open and save in; empty: the open project's folder, else Documents.</summary>
    public string WorkingFolder { get; set; } = "";

    /// <summary>
    /// Whether AI agents (the chat, and agents outside the program) may see the rows a query returns. Off until turned
    /// on: rows are often patients' or members' records. Agents always get row counts and columns.
    /// </summary>
    public bool AgentsSeeRows { get; set; }

    public bool ChatOpen { get; set; }
    public bool ChatAutoContinue { get; set; } = true;
    /// <summary>"Anthropic", or "OpenAICompatible" for Ollama, LM Studio, another computer's server or a cloud service.</summary>
    public string ChatProvider { get; set; } = "Anthropic";
    public string ChatServerUrl { get; set; } = "http://localhost:11434/v1";
    /// <summary>
    /// Where Claude's Messages API is reached: empty for Anthropic's own (api.anthropic.com), or a server that speaks the
    /// same API, such as LM Studio (http://localhost:1234) or Ollama (http://localhost:11434) on this computer or the network.
    /// </summary>
    public string ChatAnthropicUrl { get; set; } = "";
    public string ChatAnthropicModel { get; set; } = "";
    public string ChatServerModel { get; set; } = "";
    public bool ChatCoreTools { get; set; } = true;
    public bool ChatModelSeesPictures { get; set; }
    public bool ChatKeepHistory { get; set; }
    public string ChatModel { get; set; } = "";
    public Dictionary<string, double[]> ChatPrices { get; set; } = [];
    public double? ChatBudget { get; set; }

    /// <summary>The folder the settings, keys, token and saved chats are in.</summary>
    public static string Folder => FolderOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } d ? d : System.IO.Path.GetTempPath(), "AVAQueryBuilder");

    /// <summary>For tests: keep agents.json, keys, the token, connections and chats in a folder of their own.</summary>
    public static string? FolderOverride { get; set; }

    public static string Path => System.IO.Path.Combine(Folder, "agents.json");

    public static AgentSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize(File.ReadAllText(Path), AgentSettingsJson.Default.AgentSettings) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, AgentSettingsJson.Default.AgentSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Generated at build time, so the settings still read and write in an ahead-of-time build.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AgentSettings))]
internal partial class AgentSettingsJson : JsonSerializerContext;
