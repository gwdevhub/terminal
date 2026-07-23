using System.Text.Json;

namespace Slopterm.Server.Ai;

/// <summary>
/// The single shared serializer for the agent WebSocket channel. Unlike REST (which the
/// minimal-API framework serializes with web defaults automatically), the agent WS handler is
/// manual, so every SendAsync/Deserialize on that channel MUST route through this so the wire
/// stays camelCase and matches the pinned contract.
/// </summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>Inbound client frame: <c>send</c> / <c>stop</c> / <c>clear</c>.</summary>
public sealed class AgentClientMessage
{
    public string? Type { get; set; }
    public string? Mode { get; set; }
    public string? Text { get; set; }
}

/// <summary>One entry in the replayed display transcript (the <c>history</c> frame).</summary>
public sealed class ChatMessage
{
    public required string Id { get; set; }
    public required string Role { get; set; }
    public string Text { get; set; } = "";
    public required string Mode { get; set; }
    public List<ChatActivity> Activities { get; set; } = [];
}

public sealed class ChatActivity
{
    public required string Tool { get; set; }
    public required string Summary { get; set; }
}
