using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>One call in a batch or a steps file.</summary>
public sealed class BatchStep
{
    [JsonPropertyName("tool"), Description("The tool's name, such as \"add_filter\" or \"set_sort\".")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("args"), Description("Its arguments, as the tool takes them.")]
    public JsonElement? Args { get; set; }
}

/// <summary>
/// Working in steps that hold up: batches (several calls as one undo step, all or nothing, or a dry run), checkpoints
/// (a named moment to compare with or go back to; the AI Chat's Undo this reply and timeline use them), and steps
/// files (a session's changes kept with named parameters, run again with other values).
/// </summary>
public sealed partial class QueryTools
{
    private readonly Dictionary<string, (string Json, DateTime At, string? Note)> _checkpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Tool, JsonElement Args)> _recording = [];

    /// <summary>The tools a batch or a steps file may not call: ones that replace, write, look, or step the history.</summary>
    private static readonly HashSet<string> NotInBatch = new(StringComparer.Ordinal)
    {
        "batch", "run_steps", "save_steps", "get_recording", "new_query", "open_query", "save_query", "connect", "add_connection",
        "render_view", "render_checkpoint", "checkpoint", "restore_checkpoint", "undo", "redo", "diff", "run_query", "check_query",
        "export_results",
    };

    /// <summary>Notes a call that changed (or may have changed) the query, for get_recording and save_steps.</summary>
    internal void Record(string tool, IDictionary<string, JsonElement>? args)
    {
        if (NotInBatch.Contains(tool)) return;
        var o = new JsonObject();
        if (args != null) foreach (var (k, v) in args) o[k] = JsonNode.Parse(v.GetRawText());
        lock (_recording)
        {
            _recording.Add((tool, JsonSerializer.SerializeToElement(o)));
            if (_recording.Count > 500) _recording.RemoveAt(0);
        }
    }

    // ───────────────────────────── batches ─────────────────────────────

    [McpServerTool(Name = "batch")]
    [Description("Runs several calls in order as one undo step, all or nothing: if a step fails, the steps before it are taken back and the error names the step. With dry_run, says what the steps would change (entities added, removed and changed), then takes them back. Not for calls that open, save, connect, run, look, or step the history.")]
    public async Task<string> Batch(
        McpServer server,
        [Description("The calls: each a tool name and its arguments.")] List<BatchStep> steps,
        [Description("Only say what the steps would change, then take them back. Default false.")] bool dry_run = false,
        CancellationToken cancel = default) =>
        (await RunBatch(server, steps, dry_run, cancel).ConfigureAwait(false)).ToJsonString(Json.Options);

    private async Task<JsonObject> RunBatch(McpServer? server, List<BatchStep> steps, bool dryRun, CancellationToken cancel)
    {
        if (steps.Count == 0) throw new McpException("Give at least one step.");
        if (steps.Count > 200) throw new McpException("A batch is up to 200 steps.");
        foreach (var (step, i) in steps.Select((x, i) => (x, i)))
        {
            if (NotInBatch.Contains(step.Tool)) throw new McpException($"Step {i + 1}: {step.Tool} can't be in a batch (it opens, saves, connects, runs, looks or steps the history).");
            if (!Methods.ContainsKey(step.Tool)) throw new McpException($"Step {i + 1}: there's no tool \"{step.Tool}\".");
        }
        var (depth, before) = await Run("began a batch", s => (s.History.Index, QueryEdits.ToJson(s.Document)), cancel).ConfigureAwait(false);
        var answers = new JsonArray();
        for (var i = 0; i < steps.Count; i++)
        {
            try
            {
                var answer = await Call(server, steps[i], cancel).ConfigureAwait(false);
                answers.Add(JsonNode.Parse(answer));
            }
            catch (Exception e) when (e is McpException or ArgumentException or JsonException)
            {
                await Rewind(depth, cancel).ConfigureAwait(false);
                throw new McpException($"Step {i + 1} ({steps[i].Tool}) failed: {e.Message} The batch was taken back: nothing changed.");
            }
        }
        if (dryRun)
        {
            var change = await Run("compared", s => Compare(before, QueryEdits.ToJson(s.Document)), cancel).ConfigureAwait(false);
            await Rewind(depth, cancel).ConfigureAwait(false);
            return new JsonObject { ["dry_run"] = true, ["would_change"] = change, ["answers"] = answers };
        }
        await Run("finished a batch", s =>
        {
            s.History.Squash(s.History.Index - depth, $"AI: a batch of {steps.Count} steps");
            s.Refresh();
            return true;
        }, cancel).ConfigureAwait(false);
        return new JsonObject { ["steps"] = steps.Count, ["answers"] = answers };
    }

    private Task Rewind(int depth, CancellationToken cancel) =>
        Run("took a batch back", s =>
        {
            while (s.History.Index > depth && s.History.CanUndo) s.History.Undo();
            s.History.ForgetRedo();
            s.Refresh();
            return true;
        }, cancel);

    /// <summary>The tools by name, for batches and steps files.</summary>
    private static readonly Dictionary<string, MethodInfo> Methods = typeof(QueryTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name is not null && m.ReturnType == typeof(Task<string>))
        .ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, StringComparer.Ordinal);

    /// <summary>Calls a tool in this session, binding its JSON arguments to the handler's parameters by name.</summary>
    private Task<string> Call(McpServer? server, BatchStep step, CancellationToken cancel)
    {
        var method = Methods[step.Tool];
        var given = step.Args is { ValueKind: JsonValueKind.Object } a ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
        var parameters = method.GetParameters();
        foreach (var name in given.Keys)
            if (!parameters.Any(p => p.Name == name))
                throw new ArgumentException($"{step.Tool} has no argument \"{name}\". It takes: {string.Join(", ", parameters.Where(p => p.ParameterType != typeof(CancellationToken) && p.ParameterType != typeof(McpServer)).Select(p => p.Name))}.");
        var values = parameters.Select(p =>
        {
            if (p.ParameterType == typeof(CancellationToken)) return (object?)cancel;
            if (p.ParameterType == typeof(McpServer)) return server;
            if (given.TryGetValue(p.Name!, out var v)) return v.Deserialize(p.ParameterType, Json.Options);
            if (p.HasDefaultValue) return p.DefaultValue;
            throw new ArgumentException($"{step.Tool} needs \"{p.Name}\".");
        }).ToArray();
        try
        {
            return (Task<string>)method.Invoke(this, values)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is { } inner)
        {
            throw inner;
        }
    }

    // ───────────────────────────── steps files ─────────────────────────────

    [McpServerTool(Name = "get_recording", ReadOnly = true, Idempotent = true)]
    [Description("The calls this session has made that changed the query, in order, as steps (tool and arguments): what save_steps keeps.")]
    public Task<string> GetRecording(CancellationToken cancel = default)
    {
        lock (_recording)
            return Task.FromResult(new JsonObject
            {
                ["steps"] = new JsonArray(_recording.Select(r => (JsonNode)new JsonObject { ["tool"] = r.Tool, ["args"] = JsonNode.Parse(r.Args.GetRawText()) }).ToArray()),
            }.ToJsonString(Json.Options));
    }

    [McpServerTool(Name = "save_steps")]
    [Description("Keeps a sequence of calls as a steps file (.avasteps.json) in the working folder, to run again with run_steps: by default this session's recording (get_recording), or steps given. Put ${name} in any argument where a value should be filled in each time (a table, a date, a value to filter on), and give each name a default in parameters.")]
    public Task<string> SaveSteps(
        [Description("The file, relative to the working folder; .avasteps.json is added if missing.")] string path,
        [Description("The steps (tool and args). Default: this session's recording.")] List<BatchStep>? steps = null,
        [Description("Each ${name} used in the steps and its default value, e.g. {\"since\": \"2026-01-01\"}.")] Dictionary<string, JsonElement>? parameters = null,
        [Description("What the steps make, in a sentence. Optional.")] string? description = null,
        [Description("Write over an existing file. Default false.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Run("saved steps", _ =>
        {
            List<(string Tool, JsonElement Args)> list;
            if (steps is { Count: > 0 }) list = steps.Select(x => (x.Tool, x.Args ?? JsonSerializer.SerializeToElement(new { }))).ToList();
            else lock (_recording) list = [.. _recording];
            if (list.Count == 0) throw new InvalidOperationException("There are no steps to keep: this session hasn't changed the query yet, and none were given.");
            foreach (var (tool, _) in list)
                if (NotInBatch.Contains(tool) || !Methods.ContainsKey(tool)) throw new ArgumentException($"A steps file can't hold {tool}.");
            var text = JsonSerializer.Serialize(list.Select(x => new { tool = x.Tool, args = x.Args }));
            var used = System.Text.RegularExpressions.Regex.Matches(text, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
            var missing = used.Where(u => parameters?.ContainsKey(u) != true).ToList();
            if (missing.Count > 0) throw new ArgumentException($"Give a default in parameters for {string.Join(", ", missing.Select(m => "${" + m + "}"))}.");
            var full = _files.Resolve(path.EndsWith(".avasteps.json", StringComparison.OrdinalIgnoreCase) ? path : path + ".avasteps.json");
            _files.CheckWritable(full, overwrite);
            var file = new JsonObject
            {
                ["format"] = "avaquerybuilder.steps",
                ["version"] = 1,
                ["description"] = description,
                ["parameters"] = JsonNode.Parse(JsonSerializer.Serialize(parameters ?? [])),
                ["steps"] = JsonNode.Parse(text),
            };
            File.WriteAllText(full, file.ToJsonString(new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
            _files.Wrote(full);
            return new JsonObject { ["saved"] = Path.GetRelativePath(host.Root, full), ["steps"] = list.Count, ["parameters"] = new JsonArray(used.Select(u => (JsonNode)u).ToArray()) }.ToJsonString(Json.Options);
        }, cancel);

    [McpServerTool(Name = "run_steps")]
    [Description("Runs a steps file (save_steps) again as one undo step, filling its ${parameters} with the values given (the rest keep their defaults); with dry_run, says what it would change and takes it back.")]
    public async Task<string> RunSteps(
        McpServer server,
        [Description("The steps file, relative to the working folder.")] string path,
        [Description("Values for its parameters, e.g. {\"since\": \"2026-07-01\"}.")] Dictionary<string, JsonElement>? values = null,
        [Description("Only say what it would change. Default false.")] bool dry_run = false,
        CancellationToken cancel = default)
    {
        var steps = await Run("read steps", _ =>
        {
            var full = _files.Resolve(path);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no {path} in the working folder.");
            var file = JsonNode.Parse(File.ReadAllText(full)) as JsonObject ?? throw new InvalidDataException($"{path} isn't a steps file.");
            if ((string?)file["format"] != "avaquerybuilder.steps") throw new InvalidDataException($"{path} isn't an AVA Query Builder steps file.");
            var defaults = file["parameters"] as JsonObject ?? [];
            var fill = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (k, v) in defaults) fill[k] = v?.DeepClone();
            foreach (var (k, v) in values ?? [])
            {
                if (!fill.ContainsKey(k)) throw new ArgumentException($"{path} has no parameter \"{k}\". Its parameters: {string.Join(", ", fill.Keys)}.");
                fill[k] = JsonNode.Parse(v.GetRawText());
            }
            var list = (file["steps"] as JsonArray ?? []).Select(n => Substitute(n!.DeepClone(), fill)).ToList();
            return list.Select(n => new BatchStep { Tool = (string?)n["tool"] ?? "", Args = n["args"] is { } a ? JsonSerializer.SerializeToElement(a) : null }).ToList();
        }, cancel).ConfigureAwait(false);
        return (await RunBatch(server, steps, dry_run, cancel).ConfigureAwait(false)).ToJsonString(Json.Options);
    }

    /// <summary>Puts parameters' values in: a string that is just "${name}" becomes the value itself; inside text, its text.</summary>
    private static JsonNode Substitute(JsonNode node, Dictionary<string, JsonNode?> fill)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(p => p.Key).ToList())
                    if (o[k] is { } child && Substitute(child, fill) is var put && !ReferenceEquals(put, child))
                        o[k] = put;
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is { } child && Substitute(child, fill) is var put && !ReferenceEquals(put, child))
                        a[i] = put;
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("${", StringComparison.Ordinal):
                var whole = System.Text.RegularExpressions.Regex.Match(s, @"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$");
                if (whole.Success) return Lookup(whole.Groups[1].Value)?.DeepClone() ?? JsonValue.Create((string?)null)!;
                return JsonValue.Create(System.Text.RegularExpressions.Regex.Replace(s, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m =>
                    Lookup(m.Groups[1].Value) is JsonValue jv && jv.TryGetValue<string>(out var t) ? t : Lookup(m.Groups[1].Value)?.ToJsonString() ?? ""));
            default:
                return node;
        }

        JsonNode? Lookup(string name) => fill.TryGetValue(name, out var value) ? value : throw new ArgumentException($"The steps use ${{{name}}}, which has no value.");
    }

    // ───────────────────────────── checkpoints ─────────────────────────────

    [McpServerTool(Name = "checkpoint")]
    [Description("Remembers the query as it is now under a name, for this session: diff compares with it, render_checkpoint draws it, restore_checkpoint goes back to it (as one undo step). Without a name, lists the checkpoints.")]
    public Task<string> Checkpoint(
        [Description("The checkpoint's name. A name used before is replaced.")] string? name = null,
        [Description("A note on what it's for. Optional.")] string? note = null,
        [Description("Accepted for other clients; checkpoints last as long as the session.")] bool persist = false,
        CancellationToken cancel = default) =>
        Read("took a checkpoint", s =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return new JsonObject { ["checkpoints"] = new JsonArray(_checkpoints.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["note"] = kv.Value.Note, ["taken"] = kv.Value.At.ToString("HH:mm:ss") }).ToArray()) };
            _checkpoints[name.Trim()] = (QueryEdits.ToJson(s.Document), DateTime.Now, note);
            _ = persist;
            if (_checkpoints.Count > 40) _checkpoints.Remove(_checkpoints.MinBy(kv => kv.Value.At).Key);
            return new JsonObject { ["checkpoint"] = name.Trim() };
        }, cancel);

    [McpServerTool(Name = "diff", ReadOnly = true, Idempotent = true)]
    [Description("What has changed since a checkpoint: entities added, removed and changed (by id and label), and the SQL then and now.")]
    public Task<string> Diff(
        [Description("The checkpoint's name.")] string since,
        CancellationToken cancel = default) =>
        Read("compared with a checkpoint", s =>
        {
            var was = CheckpointNamed(since).Json;
            var change = Compare(was, QueryEdits.ToJson(s.Document));
            change["sql_then"] = QueryEdits.Sql(QueryEdits.FromJson(was), s.Engine);
            change["sql_now"] = QueryEdits.Sql(s.Document, s.Engine);
            return change;
        }, cancel);

    [McpServerTool(Name = "render_checkpoint", ReadOnly = true, Idempotent = true)]
    [Description("A picture of the query's diagram as it was at a checkpoint, about width by height pixels.")]
    public async Task<CallToolResult> RenderCheckpoint(
        [Description("The checkpoint's name.")] string name,
        [Description("Width in pixels, from 40 to 1600. Default 320.")] int width = 320,
        [Description("Height in pixels, from 40 to 1600. Default 240.")] int height = 240,
        CancellationToken cancel = default)
    {
        if (width is < 40 or > 1600 || height is < 40 or > 1600) throw new McpException("width and height are from 40 to 1600 pixels.");
        var png = await Run("drew a checkpoint", s =>
        {
            var doc = QueryEdits.FromJson(CheckpointNamed(name).Json);
            var w = doc.Entities.Select(e => e.X + e.Width).DefaultIfEmpty(300).Max() + 2 * QueryEdits.FirstX;
            var h = doc.Entities.Select(e => e.Y + e.Height).DefaultIfEmpty(200).Max() + 2 * QueryEdits.FirstY;
            return host.Library.Picture(doc, Math.Clamp(Math.Min(width / w, height / h), 0.05, 2));
        }, cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png")] };
    }

    [McpServerTool(Name = "restore_checkpoint", Destructive = true)]
    [Description("Puts the whole query back as it was at a checkpoint, as one undo step (so undo brings back what was there).")]
    public Task<string> RestoreCheckpoint(
        [Description("The checkpoint's name.")] string name,
        CancellationToken cancel = default) =>
        Edit("went back to a checkpoint", [], (s, doc, catalog) =>
        {
            var c = CheckpointNamed(name);
            var change = Compare(c.Json, QueryEdits.ToJson(doc));
            var was = QueryEdits.FromJson(c.Json);
            doc.Entities.Clear();
            doc.Entities.AddRange(was.Entities);
            doc.Connectors.Clear();
            doc.Connectors.AddRange(was.Connectors);
            doc.LookupOrdinal = was.LookupOrdinal;
            return new JsonObject { ["restored"] = name, ["changed"] = change["changed"]?.DeepClone(), ["undone"] = change };
        }, cancel, needsDatabase: false);

    private (string Json, DateTime At, string? Note) CheckpointNamed(string name) =>
        _checkpoints.TryGetValue(name.Trim(), out var c) ? c
        : throw new ArgumentException($"There's no checkpoint \"{name}\"" + (_checkpoints.Count > 0 ? $". The checkpoints: {string.Join(", ", _checkpoints.Keys)}." : ": checkpoint takes one."));

    /// <summary>How the query in <paramref name="now"/> differs from <paramref name="was"/> (both as saved), entity by entity.</summary>
    internal static JsonObject Compare(string was, string now)
    {
        static Dictionary<string, (string Label, string Settings)> Entities(string json)
        {
            var doc = QueryEdits.FromJson(json);
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var e in doc.Entities)
                map.TryAdd(e.Id, (e.Label, QueryEdits.Metadata(e) is { } m ? JsonSerializer.Serialize(m, m.GetType()) : ""));
            return map;
        }
        var (a, b) = (Entities(was), Entities(now));
        JsonArray Ids(IEnumerable<string> ids, Dictionary<string, (string Label, string Settings)> from) =>
            new(ids.Take(100).Select(x => (JsonNode)$"{x} ({from[x].Label})").ToArray());
        var changed = b.Keys.Where(k => a.TryGetValue(k, out var v) && v.Settings != b[k].Settings).ToList();
        return new JsonObject
        {
            ["changed"] = a.Count != b.Count || changed.Count > 0 || a.Keys.Except(b.Keys).Any(),
            ["added"] = Ids(b.Keys.Where(k => !a.ContainsKey(k)), b),
            ["removed"] = Ids(a.Keys.Where(k => !b.ContainsKey(k)), a),
            ["changed_elements"] = Ids(changed, b),
            ["entities"] = new JsonObject { ["was"] = a.Count, ["now"] = b.Count },
        };
    }
}
