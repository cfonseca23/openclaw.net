using Microsoft.Extensions.AI;

namespace OpenClaw.Core.Observability;

public readonly record struct PromptCacheUsage(long CacheReadTokens, long CacheWriteTokens)
{
    public static PromptCacheUsage Empty { get; } = new(0, 0);
}

public static class PromptCacheUsageExtractor
{
    private static readonly string[] CacheWriteKeys =
    [
        "cache_write_tokens",
        "cacheWriteTokens",
        "cache_creation_input_tokens",
        "cacheCreationInputTokens"
    ];

    public static PromptCacheUsage FromUsage(UsageDetails? usage)
    {
        if (usage is null)
            return PromptCacheUsage.Empty;

        var cacheRead = usage.CachedInputTokenCount ?? 0;
        long cacheWrite = 0;
        if (usage.AdditionalCounts is not null)
        {
            foreach (var key in CacheWriteKeys)
            {
                if (usage.AdditionalCounts.TryGetValue(key, out var value))
                {
                    cacheWrite = value;
                    break;
                }
            }
        }

        return new PromptCacheUsage(cacheRead, cacheWrite);
    }

    public static PromptCacheUsage Merge(params PromptCacheUsage[] items)
    {
        long cacheRead = 0;
        long cacheWrite = 0;
        foreach (var item in items)
        {
            cacheRead += item.CacheReadTokens;
            cacheWrite += item.CacheWriteTokens;
        }

        return new PromptCacheUsage(cacheRead, cacheWrite);
    }
}
