using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Extensions;

internal sealed class GatewaySessionCleanupWorker
{
    public void Start(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        SessionManager sessionManager)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping))
            {
                try
                {
                    var evicted = sessionManager.SweepExpiredActiveSessions();
                    if (evicted > 0)
                        logger.LogDebug("Proactive active-session sweep evicted {Count} expired sessions", evicted);

                    sessionManager.CleanupSessionLocksOnce(DateTimeOffset.UtcNow, TimeSpan.FromHours(2));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during session lock cleanup");
                }
            }
        }, lifetime.ApplicationStopping);
    }
}
