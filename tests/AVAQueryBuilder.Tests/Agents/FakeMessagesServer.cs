using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVAQueryBuilder.Tests.Agents;

/// <summary>
/// A stand-in for a server that speaks Anthropic's Messages API (LM Studio and Ollama on this computer do): an
/// <see cref="HttpMessageHandler"/> that answers each POST /v1/messages with the next scripted reply, streamed as the
/// API's server-sent events, and keeps every request and its URL to look at. Nothing leaves the process.
/// </summary>
internal sealed class FakeMessagesServer : HttpMessageHandler
{
    private readonly Queue<Part[]> _script = new();

    public List<JsonObject> Requests { get; } = [];

    public List<string> Urls { get; } = [];

    public abstract record Part;
    public sealed record Words(string Text) : Part;
    public sealed record ToolUse(string Id, string Name, object Input) : Part;

    public FakeMessagesServer Reply(params Part[] parts)
    {
        _script.Enqueue(parts);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Urls.Add(request.RequestUri!.ToString());
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancel))!.AsObject());
        var parts = _script.Count > 0 ? _script.Dequeue() : [new Words("(no more script)")];
        var sse = new StringBuilder();
        void Event(string type, object data) => sse.Append($"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n");
        Event("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_" + Requests.Count, type = "message", role = "assistant", model = "local", content = Array.Empty<object>(),
                stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 100, output_tokens = 1 },
            },
        });
        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case Words w:
                    Event("content_block_start", new { type = "content_block_start", index = i, content_block = new { type = "text", text = "" } });
                    Event("content_block_delta", new { type = "content_block_delta", index = i, delta = new { type = "text_delta", text = w.Text } });
                    break;
                case ToolUse u:
                    Event("content_block_start", new { type = "content_block_start", index = i, content_block = new { type = "tool_use", id = u.Id, name = u.Name, input = new { } } });
                    Event("content_block_delta", new { type = "content_block_delta", index = i, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(u.Input) } });
                    break;
            }
            Event("content_block_stop", new { type = "content_block_stop", index = i });
        }
        var stop = parts.Any(p => p is ToolUse) ? "tool_use" : "end_turn";
        Event("message_delta", new { type = "message_delta", delta = new { stop_reason = stop, stop_sequence = (string?)null }, usage = new { output_tokens = 20 } });
        Event("message_stop", new { type = "message_stop" });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}
