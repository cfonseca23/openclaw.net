using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Get compact status of a session including state, token usage, and duration.
/// </summary>
internal sealed class SessionStatusTool : IToolWithContext
{
    private readonly SessionManager _sessions;
    private readonly ProviderUsageTracker? _providerUsage;

    public SessionStatusTool(SessionManager sessions, ProviderUsageTracker? providerUsage = null)
    {
        _sessions = sessions;
        _providerUsage = providerUsage;
    }

    public string Name => "session_status";
    public string Description => "Get compact status of a session. Shows state, token usage, turn count, and active duration.";
    public string ParameterSchema => """{"type":"object","properties":{"session_id":{"type":"string","description":"Session ID (defaults to current session if omitted)"}}}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: session_status requires execution context.");

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var sessionId = GetString(root, "session_id") ?? context.Session.Id;
        var session = _sessions.TryGetActiveById(sessionId);

        if (session is null)
            return ValueTask.FromResult($"Session '{sessionId}' not found or not active.");

        var duration = DateTimeOffset.UtcNow - session.CreatedAt;
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {session.Id}");
        sb.AppendLine($"  State: {session.State}");
        sb.AppendLine($"  Channel: {session.ChannelId}");
        sb.AppendLine($"  Sender: {session.SenderId}");
        sb.AppendLine($"  Turns: {session.History.Count}");
        sb.AppendLine($"  Tokens (in/out): {session.TotalInputTokens}/{session.TotalOutputTokens}");
        var (cacheReadTokens, cacheWriteTokens) = GetCacheTotals(session);
        sb.AppendLine($"  Prompt Cache (read/write): {cacheReadTokens}/{cacheWriteTokens}");
        sb.AppendLine($"  Created: {session.CreatedAt:u}");
        sb.AppendLine($"  Last Active: {session.LastActiveAt:u}");
        sb.AppendLine($"  Duration: {(int)duration.TotalHours}h {duration.Minutes}m");

        if (session.ModelOverride is not null)
            sb.AppendLine($"  Model Override: {session.ModelOverride}");

        return ValueTask.FromResult(sb.ToString().TrimEnd());
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private (long CacheReadTokens, long CacheWriteTokens) GetCacheTotals(Session session)
    {
        if (session.TotalCacheReadTokens > 0 || session.TotalCacheWriteTokens > 0)
            return (session.TotalCacheReadTokens, session.TotalCacheWriteTokens);

        return _providerUsage?.GetLatestSessionCacheTotals(session.Id) ?? (0, 0);
    }
}
