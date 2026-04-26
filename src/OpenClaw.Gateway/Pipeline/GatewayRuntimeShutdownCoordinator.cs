using Microsoft.Extensions.Hosting;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Pipeline;

internal sealed class GatewayRuntimeShutdownCoordinator : IHostedService
{
    private readonly ILogger<GatewayRuntimeShutdownCoordinator> _logger;
    private readonly object _gate = new();
    private readonly List<AsyncCleanupRegistration> _asyncCleanups = [];
    private GatewayStartupContext? _startup;
    private GatewayAppRuntime? _runtime;
    private Task? _stopTask;

    public GatewayRuntimeShutdownCoordinator(ILogger<GatewayRuntimeShutdownCoordinator> logger)
    {
        _logger = logger;
    }

    public void AttachRuntime(GatewayStartupContext startup, GatewayAppRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(runtime);

        lock (_gate)
        {
            if (_runtime is not null && !ReferenceEquals(_runtime, runtime))
                throw new InvalidOperationException("Gateway runtime shutdown coordinator is already attached to a different runtime.");

            _startup = startup;
            _runtime = runtime;
        }
    }

    public void RegisterAsyncCleanup(string name, Func<CancellationToken, ValueTask> cleanup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(cleanup);

        lock (_gate)
        {
            _asyncCleanups.Add(new AsyncCleanupRegistration(name, cleanup));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync(cancellationToken);
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        GatewayStartupContext? startup;
        GatewayAppRuntime? runtime;
        AsyncCleanupRegistration[] asyncCleanups;

        lock (_gate)
        {
            startup = _startup;
            runtime = _runtime;
            asyncCleanups = [.. _asyncCleanups];
        }

        if (startup is null || runtime is null)
            return;

        _logger.LogInformation(
            "Shutdown signal received — draining in-flight requests ({Timeout}s timeout)…",
            startup.Config.GracefulShutdownSeconds);

        await DrainInflightRequestsAsync(startup, runtime, cancellationToken);
        await RunAsyncCleanupsAsync(asyncCleanups, cancellationToken);
        await DisposeRuntimeAsync(startup, runtime);
    }

    private async Task DrainInflightRequestsAsync(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (startup.Config.GracefulShutdownSeconds <= 0)
            return;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(startup.Config.GracefulShutdownSeconds);
        var checkInterval = TimeSpan.FromMilliseconds(100);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var allFree = true;
            foreach (var semaphore in runtime.SessionLocks.Values)
            {
                if (semaphore.CurrentCount == 0)
                {
                    allFree = false;
                    break;
                }
            }

            if (allFree)
                break;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(checkInterval < remaining ? checkInterval : remaining, cancellationToken);
        }

        _logger.LogInformation("Drain complete — shutting down");
    }

    private async Task RunAsyncCleanupsAsync(
        IReadOnlyList<AsyncCleanupRegistration> asyncCleanups,
        CancellationToken cancellationToken)
    {
        for (var i = asyncCleanups.Count - 1; i >= 0; i--)
        {
            var cleanup = asyncCleanups[i];
            try
            {
                await cleanup.Cleanup(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Shutdown cleanup '{Name}' was canceled.", cleanup.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shutdown cleanup '{Name}' threw an exception.", cleanup.Name);
            }
        }
    }

    private async Task DisposeRuntimeAsync(GatewayStartupContext startup, GatewayAppRuntime runtime)
    {
        var pluginDisposeTimeout = TimeSpan.FromSeconds(Math.Max(startup.Config.GracefulShutdownSeconds, 10));

        try
        {
            await runtime.SkillWatcher.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill watcher disposal threw an exception.");
        }

        await DisposeAsyncWithTimeout(runtime.PluginHost, "plugin host", pluginDisposeTimeout);
        await DisposeAsyncWithTimeout(runtime.NativeDynamicPluginHost, "native dynamic plugin host", pluginDisposeTimeout);
        await DisposeAsyncWithTimeout(runtime.WhatsAppWorkerHost, "whatsapp worker host", pluginDisposeTimeout);

        foreach (var ownerId in runtime.DynamicProviderOwners)
        {
            runtime.Operations.ProviderRegistry.UnregisterOwnedBy(ownerId);
            LlmClientFactory.UnregisterProvidersOwnedBy(ownerId);
        }

        try
        {
            runtime.NativeRegistry.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native registry disposal threw an exception.");
        }

        GatewayWorkers.DisposeSessionLocks(runtime.SessionLocks, _logger);
    }

    private async Task DisposeAsyncWithTimeout(IAsyncDisposable? disposable, string component, TimeSpan timeout)
    {
        if (disposable is null)
            return;

        try
        {
            await disposable.DisposeAsync().AsTask().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Component} disposal timed out after {Seconds}s.", component, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Component} disposal threw an exception.", component);
        }
    }

    private readonly record struct AsyncCleanupRegistration(string Name, Func<CancellationToken, ValueTask> Cleanup);
}
