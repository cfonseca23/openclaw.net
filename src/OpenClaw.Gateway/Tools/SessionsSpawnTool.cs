using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Spawn a new sub-agent session with an initial prompt.
/// </summary>
internal sealed class SessionsSpawnTool : IToolWithContext
{
    private readonly SessionManager _sessions;
    private readonly MessagePipeline _pipeline;

    public SessionsSpawnTool(SessionManager sessions, MessagePipeline pipeline)
    {
        _sessions = sessions;
        _pipeline = pipeline;
    }

    public string Name => "sessions_spawn";
    public string Description => "Spawn a new agent session with an initial prompt. Returns the new session ID.";
    public string ParameterSchema => """{"type":"object","properties":{"prompt":{"type":"string","description":"Initial prompt for the new session"},"session_id":{"type":"string","description":"Optional explicit session ID (auto-generated if omitted)"},"channel_id":{"type":"string","description":"Channel to associate (default: 'agent')"}},"required":["prompt"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: sessions_spawn requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var prompt = GetString(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: 'prompt' is required.";

        var channelId = GetString(root, "channel_id") ?? "agent";
        var sessionId = GetString(root, "session_id") ?? $"spawn_{Guid.NewGuid():N}"[..20];

        var session = await _sessions.GetOrCreateByIdAsync(sessionId, channelId, "system", ct);

        var inbound = new InboundMessage
        {
            ChannelId = channelId,
            SenderId = "system",
            SessionId = session.Id,
            Text = prompt,
            IsSystem = true,
        };

        await _pipeline.InboundWriter.WriteAsync(inbound, ct);
        return $"Spawned session '{session.Id}' on channel '{channelId}'.";
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
