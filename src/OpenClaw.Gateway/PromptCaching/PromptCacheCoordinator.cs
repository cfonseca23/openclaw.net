using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.PromptCaching;

internal sealed class PromptCachePreparedRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required ChatOptions Options { get; init; }
    public required PromptCacheDescriptor Descriptor { get; init; }
}

internal sealed class PromptCacheDescriptor
{
    public required string SessionId { get; init; }
    public required string ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string Dialect { get; init; }
    public required string Retention { get; init; }
    public required string StableFingerprint { get; init; }
    public required string StableSystemPrompt { get; init; }
    public required string VolatileSuffix { get; init; }
    public required string ToolSignature { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public bool Enabled { get; init; }
    public bool KeepWarmEligible { get; init; }
}

internal sealed class PromptCacheWarmCandidate
{
    public required PromptCacheDescriptor Descriptor { get; init; }
    public required IReadOnlyList<ChatMessage> WarmMessages { get; init; }
    public required ChatOptions WarmOptions { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastWarmedAtUtc { get; set; }
}

internal sealed class PromptCacheWarmRegistry
{
    private readonly ConcurrentDictionary<string, PromptCacheWarmCandidate> _entries = new(StringComparer.Ordinal);

    public void Record(PromptCachePreparedRequest request)
    {
        if (!request.Descriptor.Enabled || !request.Descriptor.KeepWarmEligible || string.IsNullOrWhiteSpace(request.Descriptor.StableSystemPrompt))
            return;

        var key = BuildKey(request.Descriptor.SessionId, request.Descriptor.ProfileId);
        _entries[key] = new PromptCacheWarmCandidate
        {
            Descriptor = request.Descriptor,
            WarmMessages = [new ChatMessage(ChatRole.System, request.Descriptor.StableSystemPrompt)],
            WarmOptions = new ChatOptions
            {
                ModelId = request.Options.ModelId,
                Tools = request.Options.Tools,
                ResponseFormat = request.Options.ResponseFormat,
                AdditionalProperties = request.Options.AdditionalProperties?.Clone(),
                MaxOutputTokens = 1,
                Temperature = 0
            },
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };
    }

    public IReadOnlyList<PromptCacheWarmCandidate> Snapshot() => _entries.Values.ToArray();

    public void MarkWarmed(PromptCacheWarmCandidate candidate, DateTimeOffset warmedAtUtc)
    {
        var key = BuildKey(candidate.Descriptor.SessionId, candidate.Descriptor.ProfileId);
        if (_entries.TryGetValue(key, out var current))
            current.LastWarmedAtUtc = warmedAtUtc;
    }

    public void Prune(IReadOnlySet<string> activeSessionIds, DateTimeOffset staleBeforeUtc)
    {
        foreach (var entry in _entries)
        {
            if (!activeSessionIds.Contains(entry.Value.Descriptor.SessionId) || entry.Value.LastSeenAtUtc < staleBeforeUtc)
                _entries.TryRemove(entry.Key, out _);
        }
    }

    private static string BuildKey(string sessionId, string profileId) => $"{sessionId}:{profileId}";
}

internal sealed class PromptCacheCoordinator
{
    private const string RouteInstructionsMarker = "\n\n[Route Instructions]\n";
    private readonly GatewayConfig _config;
    private readonly PromptCacheTraceWriter _traceWriter;

    public PromptCacheCoordinator(GatewayConfig config, PromptCacheTraceWriter traceWriter)
    {
        _config = config;
        _traceWriter = traceWriter;
    }

    public PromptCachePreparedRequest Prepare(
        Session session,
        ModelProfile profile,
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options)
    {
        var caching = profile.PromptCaching;
        var dialect = ResolveDialect(profile.ProviderId, caching.Dialect);
        var retention = NormalizeRetention(caching.Retention);
        var (stableSystem, volatileSuffix) = ExtractSystemPromptSegments(messages);
        var toolSignature = BuildToolSignature(options);
        var stableFingerprint = BuildStableFingerprint(profile.ProviderId, modelId, stableSystem, toolSignature, options.ResponseFormat);
        var preparedOptions = CloneOptions(options);
        preparedOptions.ModelId = modelId;

        if (caching.Enabled == true && dialect != "none" && profile.Capabilities.SupportsPromptCaching)
        {
            preparedOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            preparedOptions.AdditionalProperties["openclaw_prompt_cache_enabled"] = true;
            preparedOptions.AdditionalProperties["openclaw_prompt_cache_dialect"] = dialect;
            preparedOptions.AdditionalProperties["openclaw_prompt_cache_retention"] = retention;
            preparedOptions.AdditionalProperties["openclaw_prompt_cache_fingerprint"] = stableFingerprint;
            preparedOptions.AdditionalProperties["openclaw_prompt_cache_keep_warm"] = caching.KeepWarmEnabled == true;

            switch (dialect)
            {
                case "openai":
                    preparedOptions.AdditionalProperties["prompt_cache_key"] = stableFingerprint;
                    if (retention == "long")
                        preparedOptions.AdditionalProperties["prompt_cache_retention"] = "24h";
                    break;
                case "anthropic":
                    preparedOptions.AdditionalProperties["anthropic_cache_key"] = stableFingerprint;
                    preparedOptions.AdditionalProperties["anthropic_cache_control"] = retention == "long" ? "1h" : "ephemeral";
                    break;
                case "gemini":
                    preparedOptions.AdditionalProperties["gemini_cached_content_key"] = stableFingerprint;
                    break;
            }
        }

        var descriptor = new PromptCacheDescriptor
        {
            SessionId = session.Id,
            ProfileId = profile.Id,
            ProviderId = profile.ProviderId,
            ModelId = modelId,
            Dialect = dialect,
            Retention = retention,
            StableFingerprint = stableFingerprint,
            StableSystemPrompt = stableSystem,
            VolatileSuffix = volatileSuffix,
            ToolSignature = toolSignature,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Enabled = caching.Enabled == true && dialect != "none" && profile.Capabilities.SupportsPromptCaching,
            KeepWarmEligible = caching.KeepWarmEnabled == true && SupportsKeepWarm(profile.ProviderId, dialect)
        };

        _traceWriter.WriteRequest(descriptor, messages, preparedOptions);
        return new PromptCachePreparedRequest
        {
            Messages = messages,
            Options = preparedOptions,
            Descriptor = descriptor
        };
    }

    public void RecordResponse(PromptCacheDescriptor descriptor, long cacheReadTokens, long cacheWriteTokens)
        => _traceWriter.WriteResponse(descriptor, cacheReadTokens, cacheWriteTokens);

    public static string ResolveDialect(string providerId, string? configuredDialect)
    {
        var dialect = (configuredDialect ?? "auto").Trim().ToLowerInvariant();
        if (dialect != "auto")
            return dialect;

        var provider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        return provider switch
        {
            "openai" or "azure-openai" => "openai",
            "anthropic" or "claude" or "anthropic-vertex" or "amazon-bedrock" => "anthropic",
            "gemini" or "google" => "gemini",
            _ => "none"
        };
    }

    private static bool SupportsKeepWarm(string providerId, string dialect)
    {
        var provider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        return dialect == "anthropic" && provider is "anthropic" or "claude" or "anthropic-vertex" or "amazon-bedrock"
            || dialect == "gemini" && provider is "gemini" or "google";
    }

    private static string NormalizeRetention(string? retention)
    {
        var value = (retention ?? "auto").Trim().ToLowerInvariant();
        return value is "none" or "short" or "long" ? value : "auto";
    }

    private static (string StableSystemPrompt, string VolatileSuffix) ExtractSystemPromptSegments(IReadOnlyList<ChatMessage> messages)
    {
        var firstSystem = messages.FirstOrDefault(static message => message.Role == ChatRole.System)?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(firstSystem))
            return (string.Empty, string.Empty);

        var markerIndex = firstSystem.IndexOf(RouteInstructionsMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return (NormalizeText(firstSystem), string.Empty);

        return (
            NormalizeText(firstSystem[..markerIndex]),
            NormalizeText(firstSystem[(markerIndex + RouteInstructionsMarker.Length)..]));
    }

    private static string BuildToolSignature(ChatOptions options)
    {
        if (options.Tools is null || options.Tools.Count == 0)
            return string.Empty;

        var signatures = options.Tools
            .Select(static tool =>
            {
                var schema = tool is AIFunctionDeclaration declaration ? declaration.JsonSchema.GetRawText() : string.Empty;
                return $"{tool.Name}|{tool.Description}|{schema}";
            })
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        return string.Join("\n", signatures);
    }

    private static string BuildStableFingerprint(string providerId, string modelId, string stableSystem, string toolSignature, ChatResponseFormat? responseFormat)
    {
        var responseFormatSignature = responseFormat is null ? string.Empty : responseFormat.GetType().FullName ?? responseFormat.ToString() ?? string.Empty;
        var payload = string.Join("\n---\n", NormalizeText(providerId), NormalizeText(modelId), stableSystem, toolSignature, responseFormatSignature);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static ChatOptions CloneOptions(ChatOptions source)
        => new()
        {
            ConversationId = source.ConversationId,
            Instructions = source.Instructions,
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
            TopP = source.TopP,
            TopK = source.TopK,
            FrequencyPenalty = source.FrequencyPenalty,
            PresencePenalty = source.PresencePenalty,
            Seed = source.Seed,
            Reasoning = source.Reasoning,
            ResponseFormat = source.ResponseFormat,
            ModelId = source.ModelId,
            StopSequences = source.StopSequences?.ToList(),
            AllowMultipleToolCalls = source.AllowMultipleToolCalls,
            ToolMode = source.ToolMode,
            Tools = source.Tools,
            AdditionalProperties = source.AdditionalProperties?.Clone()
        };
}
