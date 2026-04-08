using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway.PromptCaching;

internal sealed class PromptCacheWarmService : BackgroundService
{
    private readonly SessionManager _sessions;
    private readonly ConfiguredModelProfileRegistry _profiles;
    private readonly PromptCacheWarmRegistry _warmRegistry;
    private readonly RuntimeMetrics _metrics;
    private readonly RuntimeEventStore _eventStore;
    private readonly ILogger<PromptCacheWarmService> _logger;

    public PromptCacheWarmService(
        SessionManager sessions,
        ConfiguredModelProfileRegistry profiles,
        PromptCacheWarmRegistry warmRegistry,
        RuntimeMetrics metrics,
        RuntimeEventStore eventStore,
        ILogger<PromptCacheWarmService> logger)
    {
        _sessions = sessions;
        _profiles = profiles;
        _warmRegistry = warmRegistry;
        _metrics = metrics;
        _eventStore = eventStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.IncrementPromptCacheWarmFailures();
                _logger.LogWarning(ex, "Prompt cache warm sweep failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        var activeSessionIds = (await _sessions.ListActiveAsync(ct))
            .Select(static session => session.Id)
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        _warmRegistry.Prune(activeSessionIds, now - TimeSpan.FromHours(6));

        foreach (var candidate in _warmRegistry.Snapshot())
        {
            if (!activeSessionIds.Contains(candidate.Descriptor.SessionId))
            {
                _metrics.IncrementPromptCacheWarmSkips();
                continue;
            }

            if (!_profiles.TryGetRegistration(candidate.Descriptor.ProfileId, out var registration) || registration?.Client is null)
            {
                _metrics.IncrementPromptCacheWarmSkips();
                continue;
            }

            var intervalMinutes = Math.Max(5, registration.Profile.PromptCaching.KeepWarmIntervalMinutes);
            if (candidate.LastWarmedAtUtc is not null && now - candidate.LastWarmedAtUtc < TimeSpan.FromMinutes(intervalMinutes))
            {
                _metrics.IncrementPromptCacheWarmSkips();
                continue;
            }

            try
            {
                await registration.Client.GetResponseAsync(candidate.WarmMessages, candidate.WarmOptions, ct);
                candidate.LastWarmedAtUtc = now;
                _warmRegistry.MarkWarmed(candidate, now);
                _metrics.IncrementPromptCacheWarmRuns();
                _eventStore.Append(new Core.Models.RuntimeEventEntry
                {
                    Id = $"evt_{Guid.NewGuid():N}"[..20],
                    SessionId = candidate.Descriptor.SessionId,
                    Component = "prompt_cache",
                    Action = "warm",
                    Severity = "info",
                    Summary = $"Prompt cache warmed for profile '{candidate.Descriptor.ProfileId}'.",
                    Metadata = new Dictionary<string, string>
                    {
                        ["profileId"] = candidate.Descriptor.ProfileId,
                        ["providerId"] = candidate.Descriptor.ProviderId,
                        ["modelId"] = candidate.Descriptor.ModelId,
                        ["fingerprint"] = candidate.Descriptor.StableFingerprint
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _metrics.IncrementPromptCacheWarmFailures();
                _logger.LogDebug(ex, "Prompt cache warm attempt failed for profile {ProfileId}", candidate.Descriptor.ProfileId);
                _eventStore.Append(new Core.Models.RuntimeEventEntry
                {
                    Id = $"evt_{Guid.NewGuid():N}"[..20],
                    SessionId = candidate.Descriptor.SessionId,
                    Component = "prompt_cache",
                    Action = "warm_failed",
                    Severity = "warning",
                    Summary = ex.Message,
                    Metadata = new Dictionary<string, string>
                    {
                        ["profileId"] = candidate.Descriptor.ProfileId,
                        ["providerId"] = candidate.Descriptor.ProviderId,
                        ["modelId"] = candidate.Descriptor.ModelId
                    }
                });
            }
        }
    }
}
