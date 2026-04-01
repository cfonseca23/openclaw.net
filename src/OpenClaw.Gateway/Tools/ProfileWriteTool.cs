using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Write or update user profile data. Complements profile_read.
/// </summary>
internal sealed class ProfileWriteTool : IToolWithContext
{
    private readonly IUserProfileStore _store;

    public ProfileWriteTool(IUserProfileStore store) => _store = store;

    public string Name => "profile_write";
    public string Description => "Create or update a user profile. Set summary, tone, preferences, and other profile fields.";
    public string ParameterSchema => """{"type":"object","properties":{"actor_id":{"type":"string","description":"Actor ID (defaults to current session's channel:sender)"},"summary":{"type":"string","description":"Brief user summary"},"tone":{"type":"string","description":"Preferred communication tone"},"preferences":{"type":"array","items":{"type":"string"},"description":"User preferences list"},"active_projects":{"type":"array","items":{"type":"string"},"description":"Active project names"}},"required":[]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: profile_write requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var actorId = GetString(root, "actor_id") ?? $"{context.Session.ChannelId}:{context.Session.SenderId}";

        // Load existing profile to merge
        var existing = await _store.GetProfileAsync(actorId, ct);

        var summary = GetString(root, "summary") ?? existing?.Summary ?? "";
        var tone = GetString(root, "tone") ?? existing?.Tone ?? "";
        var preferences = GetStringArray(root, "preferences") ?? existing?.Preferences ?? [];
        var activeProjects = GetStringArray(root, "active_projects") ?? existing?.ActiveProjects ?? [];

        var profile = new UserProfile
        {
            ActorId = actorId,
            ChannelId = existing?.ChannelId ?? context.Session.ChannelId,
            SenderId = existing?.SenderId ?? context.Session.SenderId,
            Summary = summary,
            Tone = tone,
            Preferences = preferences,
            ActiveProjects = activeProjects,
            Facts = existing?.Facts ?? [],
            RecentIntents = existing?.RecentIntents ?? [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _store.SaveProfileAsync(profile, ct);
        return $"Profile updated for '{actorId}'.";
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static IReadOnlyList<string>? GetStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString()!);
        }
        return list;
    }
}
