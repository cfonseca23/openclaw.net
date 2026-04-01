using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Send a message to another session. Enables cross-session communication.
/// </summary>
internal sealed class SessionsSendTool : IToolWithContext
{
    private readonly SessionManager _sessions;
    private readonly MessagePipeline _pipeline;

    public SessionsSendTool(SessionManager sessions, MessagePipeline pipeline)
    {
        _sessions = sessions;
        _pipeline = pipeline;
    }

    public string Name => "sessions_send";
    public string Description => "Send a message to another active session. The message will be processed as if it came from the system.";
    public string ParameterSchema => """{"type":"object","properties":{"session_id":{"type":"string","description":"Target session ID to send message to"},"message":{"type":"string","description":"Message text to send"}},"required":["session_id","message"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: sessions_send requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var sessionId = GetString(root, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: 'session_id' is required.";

        var message = GetString(root, "message");
        if (string.IsNullOrWhiteSpace(message))
            return "Error: 'message' is required.";

        var target = _sessions.TryGetActiveById(sessionId);
        if (target is null)
            return $"Error: Session '{sessionId}' not found or not active.";

        var inbound = new InboundMessage
        {
            ChannelId = target.ChannelId,
            SenderId = "system",
            SessionId = sessionId,
            Text = message,
            IsSystem = true,
        };

        await _pipeline.InboundWriter.WriteAsync(inbound, ct);
        return $"Message sent to session '{sessionId}'.";
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
