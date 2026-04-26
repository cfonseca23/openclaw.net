using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Extensions;

internal sealed class GatewayOutboundDeliveryWorker
{
    public void Start(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        MessagePipeline pipeline,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        HeartbeatService heartbeatService)
    {
        for (var j = 0; j < workerCount; j++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.OutboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                    while (pipeline.OutboundReader.TryRead(out var outbound))
                    {
                        if (!channelAdapters.TryGetValue(outbound.ChannelId, out var adapter))
                        {
                            logger.LogWarning("Unknown channel {ChannelId} for outbound message to {RecipientId}", outbound.ChannelId, outbound.RecipientId);
                            continue;
                        }

                        const int maxDeliveryAttempts = 2;
                        for (var attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
                        {
                            try
                            {
                                await adapter.SendAsync(outbound, lifetime.ApplicationStopping);
                                if (heartbeatService.IsManagedHeartbeatJob(outbound.CronJobName))
                                    heartbeatService.RecordDeliverySucceeded(outbound.SessionId);
                                break;
                            }
                            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception ex)
                            {
                                if (attempt < maxDeliveryAttempts)
                                {
                                    logger.LogWarning(ex, "Outbound send failed for channel {ChannelId}, retrying…", outbound.ChannelId);
                                    await Task.Delay(500, lifetime.ApplicationStopping);
                                }
                                else
                                {
                                    logger.LogError(ex, "Outbound send failed for channel {ChannelId} after {Attempts} attempts", outbound.ChannelId, maxDeliveryAttempts);
                                }
                            }
                        }
                    }
                }
            }, lifetime.ApplicationStopping);
        }
    }
}
