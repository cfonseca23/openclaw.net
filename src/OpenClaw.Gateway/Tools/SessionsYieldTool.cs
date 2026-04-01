using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Yield execution to another session. Sends a message and waits for the target session's response.
/// </summary>
internal sealed class SessionsYieldTool : IToolWithContext
{
    private readonly SessionManager _sessions;
    private readonly MessagePipeline _pipeline;
    private readonly IMemoryStore _store;

    public SessionsYieldTool(SessionManager sessions, MessagePipeline pipeline, IMemoryStore store)
    {
        _sessions = sessions;
        _pipeline = pipeline;
        _store = store;
    }

    public string Name => "sessions_yield";
    public string Description => "Yield execution to another session. Sends a message and waits for the target to respond, then returns the response.";
    public string ParameterSchema => """{"type":"object","properties":{"session_id":{"type":"string","description":"Target session ID to yield to"},"message":{"type":"string","description":"Message to send to the target session"},"timeout_seconds":{"type":"integer","description":"Max seconds to wait for response (default 60, max 300)"}},"required":["session_id","message"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: sessions_yield requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var sessionId = GetString(root, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: 'session_id' is required.";

        // Prevent self-yield deadlock
        if (string.Equals(sessionId, context.Session.Id, StringComparison.Ordinal))
            return "Error: Cannot yield to the current session (would deadlock).";

        var message = GetString(root, "message");
        if (string.IsNullOrWhiteSpace(message))
            return "Error: 'message' is required.";

        var timeoutSeconds = 60;
        if (root.TryGetProperty("timeout_seconds", out var ts) && ts.ValueKind == JsonValueKind.Number)
            timeoutSeconds = Math.Clamp(ts.GetInt32(), 5, 300);

        // Verify target session exists
        var target = _sessions.TryGetActiveById(sessionId);
        if (target is null)
            return $"Error: Session '{sessionId}' not found or not active.";

        // Record the target's current turn count to detect new responses
        var turnCountBefore = target.History.Count;

        // Send message to target session
        var inbound = new InboundMessage
        {
            ChannelId = target.ChannelId,
            SenderId = "system",
            SessionId = sessionId,
            Text = message,
            IsSystem = true,
        };

        await _pipeline.InboundWriter.WriteAsync(inbound, ct);

        // Poll for a new assistant turn on the target session
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var pollDelay = 500;
            var storeFallbackDone = false;
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await Task.Delay(pollDelay, timeoutCts.Token);
                pollDelay = Math.Min(pollDelay + 250, 2000); // backoff to 2s max

                // Check active sessions first (O(n) but avoids disk I/O)
                var updated = _sessions.TryGetActiveById(sessionId);

                // Only try store once if session was evicted from active cache
                if (updated is null && !storeFallbackDone)
                {
                    updated = await _store.GetSessionAsync(sessionId, timeoutCts.Token);
                    storeFallbackDone = true;
                }

                if (updated is not null && updated.History.Count > turnCountBefore)
                {
                    for (var i = updated.History.Count - 1; i >= turnCountBefore; i--)
                    {
                        if (string.Equals(updated.History[i].Role, "assistant", StringComparison.Ordinal))
                            return $"[Session {sessionId} responded]:\n{updated.History[i].Content}";
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout — not cancellation of parent
        }

        return $"Timeout: Session '{sessionId}' did not respond within {timeoutSeconds} seconds.";
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
