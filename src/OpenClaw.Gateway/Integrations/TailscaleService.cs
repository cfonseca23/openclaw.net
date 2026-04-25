using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Integrations;

/// <summary>
/// Manages Tailscale Serve/Funnel for zero-config remote access to the gateway.
/// </summary>
internal sealed class TailscaleService : IAsyncDisposable
{
    private readonly TailscaleConfig _config;
    private readonly int _gatewayPort;
    private readonly ILogger<TailscaleService> _logger;
    private readonly Func<CancellationToken, Task<bool>> _availabilityCheck;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string Output)>> _commandRunner;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _started;
    private bool _disposed;

    public TailscaleService(
        TailscaleConfig config,
        int gatewayPort,
        ILogger<TailscaleService> logger,
        Func<CancellationToken, Task<bool>>? availabilityCheck = null,
        Func<string, CancellationToken, Task<(int ExitCode, string Output)>>? commandRunner = null)
    {
        _config = config;
        _gatewayPort = gatewayPort;
        _logger = logger;
        _availabilityCheck = availabilityCheck ?? IsTailscaleAvailableAsync;
        _commandRunner = commandRunner ?? RunTailscaleAsync;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_config.Enabled || string.Equals(_config.Mode, "off", StringComparison.OrdinalIgnoreCase))
            return;

        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            if (!await _availabilityCheck(ct))
            {
                _logger.LogWarning("Tailscale is not available on this system. Skipping Serve/Funnel setup.");
                return;
            }

            var mode = _config.Mode.ToLowerInvariant();
            var target = $"https+insecure://localhost:{_gatewayPort}";
            var port = _config.Port > 0 ? _config.Port : 443;

            try
            {
                if (mode == "funnel")
                {
                    await _commandRunner($"funnel --bg --https={port} {target}", ct);
                    _logger.LogInformation("Tailscale Funnel started on port {Port} → localhost:{GatewayPort}.", port, _gatewayPort);
                }
                else
                {
                    await _commandRunner($"serve --bg --https={port} {target}", ct);
                    _logger.LogInformation("Tailscale Serve started on port {Port} → localhost:{GatewayPort}.", port, _gatewayPort);
                }

                _started = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start Tailscale {Mode}.", mode);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var shouldStop = false;
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            shouldStop = _started;
            _started = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (!shouldStop)
            return;

        try
        {
            await _commandRunner("serve off", CancellationToken.None);
            _logger.LogInformation("Tailscale Serve/Funnel stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop Tailscale Serve/Funnel.");
        }
    }

    private static async Task<bool> IsTailscaleAvailableAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunTailscaleAsync("status --json", ct);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunTailscaleAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "tailscale",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output);
    }
}
